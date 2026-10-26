using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using VelociHub.Native;

namespace VelociHub.Hid
{
    /// <summary>
    /// 一条打开的 HID 通道（单个集合）。写入使用「无 ReportID 前缀的定长报告」，
    /// 与浏览器 WebHID sendReport(0, 64 bytes) 的行为保持一致。
    ///
    /// 内存安全约定（历史教训）：所有 overlapped I/O 的缓冲区与 OVERLAPPED 结构
    /// 一律用 AllocHGlobal 申请，并且在确认 I/O 真正结束（含取消后等待内核收尾）
    /// 之前不释放。早期版本把托管数组直接交给 ReadFile/WriteFile，
    /// 返回 ERROR_IO_PENDING 后数组被 GC 移动、驱动仍往旧地址写，
    /// 造成托管堆被踩坏，最终在 WPF 文本排版等无关位置随机 fail-fast（0x80131506）。
    /// </summary>
    public sealed class HidChannel : IDisposable
    {
        private static readonly int OverlappedSize = Marshal.SizeOf<NativeMethods.NativeOverlappedStruct>();

        private readonly SafeFileHandle _handle;
        private readonly int _inputLength;
        private readonly int _outputLength;
        private readonly object _writeLock = new();
        private Thread? _reader;
        private volatile bool _running;

        public HidInterfaceInfo Info { get; }

        public event Action<byte[]>? ReportReceived;
        public event Action<string>? ErrorOccurred;

        public bool IsOpen => !_handle.IsInvalid && _running;

        public HidChannel(HidInterfaceInfo info)
        {
            Info = info;
            _inputLength = Math.Max(8, (int)info.InputReportLength);
            _outputLength = Math.Max(8, (int)info.OutputReportLength);

            _handle = NativeMethods.CreateFile(info.Path,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (_handle.IsInvalid)
                throw new InvalidOperationException($"无法打开 HID 设备 (Win32={Marshal.GetLastWin32Error()})");
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "mx300-hid-read" };
            _reader.Start();
        }

        public int OutputLength => _outputLength;

        /// <summary>发送一条报告。data 长度不足时会补零到报告长度。</summary>
        public bool Send(byte[] data, int? reportId = null)
        {
            int id = reportId ?? 0;
            // 该设备 InputReportByteLength / OutputReportByteLength 均为 65：
            // 首字节是 Report ID（0x00），真正的 64 字节协议帧从 [1] 开始。
            int offset = _outputLength > data.Length ? 1 : 0;
            byte[] frame = new byte[_outputLength];
            if (offset == 1) frame[0] = (byte)id;
            Array.Copy(data, 0, frame, offset, Math.Min(data.Length, _outputLength - offset));

            lock (_writeLock)
            {
                if (_handle.IsInvalid) return false;

                IntPtr buf = Marshal.AllocHGlobal(_outputLength);
                IntPtr ov = Marshal.AllocHGlobal(OverlappedSize);
                IntPtr evt = IntPtr.Zero;
                bool issued = false;      // 内核是否已接下这次 I/O
                bool completed = false;   // 是否已确认收尾
                try
                {
                    Marshal.Copy(frame, 0, buf, frame.Length);

                    evt = NativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
                    if (evt == IntPtr.Zero) return false;
                    InitOverlapped(ov, evt);

                    issued = true;
                    bool ok = NativeMethods.WriteFile(_handle, buf, (uint)_outputLength, IntPtr.Zero, ov);
                    if (ok)
                    {
                        completed = true;
                        return true;
                    }

                    int err = Marshal.GetLastWin32Error();
                    if (err != NativeMethods.ERROR_IO_PENDING)
                    {
                        completed = true;   // 同步失败，内核没有在途 I/O
                        ErrorOccurred?.Invoke($"写入失败 Win32={err}");
                        return false;
                    }

                    uint w = NativeMethods.WaitForSingleObject(evt, 1000);
                    if (w != NativeMethods.WAIT_OBJECT_0)
                    {
                        ErrorOccurred?.Invoke("写入超时");
                        return false;
                    }

                    if (!NativeMethods.GetOverlappedResult(_handle, ov, out uint written, false) || written == 0)
                    {
                        completed = true;
                        ErrorOccurred?.Invoke("写入未完成");
                        return false;
                    }
                    completed = true;
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke("写入异常: " + ex.Message);
                    return false;
                }
                finally
                {
                    // 还没有确认结束时，先取消并**等内核真正收尾**，再释放内存。
                    if (issued && !completed && !_handle.IsInvalid)
                    {
                        try
                        {
                            NativeMethods.CancelIoEx(_handle, ov);
                            NativeMethods.GetOverlappedResult(_handle, ov, out _, true);
                        }
                        catch { }
                    }
                    if (evt != IntPtr.Zero) NativeMethods.CloseHandle(evt);
                    Marshal.FreeHGlobal(ov);
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        private void ReadLoop()
        {
            int softErrors = 0;

            while (_running)
            {
                IntPtr buf = Marshal.AllocHGlobal(_inputLength);
                IntPtr ov = Marshal.AllocHGlobal(OverlappedSize);
                IntPtr evt = IntPtr.Zero;
                bool issued = false;
                bool completed = false;

                try
                {
                    evt = NativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
                    if (evt == IntPtr.Zero) break;
                    InitOverlapped(ov, evt);

                    uint read = 0;
                    bool got = false;
                    bool timedOut = false;

                    issued = true;
                    bool ok = NativeMethods.ReadFile(_handle, buf, (uint)_inputLength, IntPtr.Zero, ov);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != NativeMethods.ERROR_IO_PENDING)
                        {
                            completed = true;   // 同步失败，没有在途 I/O
                            if (_running) ErrorOccurred?.Invoke($"读取失败 Win32={err}");
                            break;
                        }

                        uint w = NativeMethods.WaitForSingleObject(evt, 250);
                        if (w == NativeMethods.WAIT_TIMEOUT)
                        {
                            // 没有数据可读是常态（设备平时不出声），不算错误。
                            timedOut = true;
                            // 取消后必须等到内核真正结束这次读，否则它可能在我们
                            // 释放 buf/ov 之后才收尾，成为纯粹的野写。
                            NativeMethods.CancelIoEx(_handle, ov);
                            NativeMethods.GetOverlappedResult(_handle, ov, out _, true);
                        }
                        else
                        {
                            got = NativeMethods.GetOverlappedResult(_handle, ov, out read, false);
                        }
                        completed = true;
                    }
                    else
                    {
                        got = NativeMethods.GetOverlappedResult(_handle, ov, out read, false);
                        completed = true;
                    }

                    if (timedOut) continue;   // finally 会清理本轮资源

                    if (got && read > 0)
                    {
                        softErrors = 0;
                        int n = (int)Math.Min(read, (uint)_inputLength);
                        var copy = new byte[n];
                        Marshal.Copy(buf, copy, 0, n);
                        try { ReportReceived?.Invoke(copy); } catch (Exception ex) { Debug.WriteLine(ex); }
                    }
                    else
                    {
                        softErrors++;
                        if (softErrors > 8)
                        {
                            if (_running) ErrorOccurred?.Invoke("读取流已中断");
                            break;
                        }
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    if (!_running) break;
                    softErrors++;
                    if (softErrors > 8) break;
                    Thread.Sleep(10);
                }
                finally
                {
                    if (issued && !completed && !_handle.IsInvalid)
                    {
                        try
                        {
                            NativeMethods.CancelIoEx(_handle, ov);
                            NativeMethods.GetOverlappedResult(_handle, ov, out _, true);
                        }
                        catch { }
                    }
                    if (evt != IntPtr.Zero) NativeMethods.CloseHandle(evt);
                    Marshal.FreeHGlobal(ov);
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        /// <summary>把一块未初始化的 OVERLAPPED 清零并挂上事件。</summary>
        private static void InitOverlapped(IntPtr ov, IntPtr hEvent)
        {
            for (int i = 0; i < OverlappedSize; i += 8) Marshal.WriteInt64(ov, i, 0);
            // OVERLAPPED: Internal(8) InternalHigh(8) Offset(4) OffsetHigh(4) hEvent(ptr)
            Marshal.WriteIntPtr(ov, IntPtr.Size * 2 + 8, hEvent);
        }

        public void Dispose()
        {
            _running = false;
            try { NativeMethods.CancelIoEx(_handle, IntPtr.Zero); } catch { }
            // 必须等读线程退出后再关句柄，否则它可能正处在一次在途读取里。
            try { _reader?.Join(800); } catch { }
            try { if (!_handle.IsInvalid) _handle.Dispose(); } catch { }
        }
    }
}
