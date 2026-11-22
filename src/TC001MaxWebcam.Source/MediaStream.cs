// Adapted from VCamNetSample (https://github.com/smourier/VCamNetSample), VCamNetSampleSource/MediaStream.cs.
// Copyright (c) 2024-2026 Simon Mourier. MIT License, see THIRD_PARTY_NOTICES.txt.
// Changes: namespace; 640 x 480 at Shared.FramesPerSecond; frames come from FrameSource
// (our own TC001 Max reader) instead of the Direct2D test pattern, and the reader runs from Start
// to Stop; CPU only (the Direct3D manager is
// accepted but not used, the sample's documented way to force CPU); correct NV12 default stride;
// RequestSample is paced to the advertised frame rate.

using System;
using System.Threading;
using DirectN;
using TC001MaxWebcam.Utilities;

namespace TC001MaxWebcam
{
    public class MediaStream : MFAttributes, IMFMediaStream2, IKsControl
    {
        public const uint NUM_IMAGE_COLS = Shared.Width;
        public const uint NUM_IMAGE_ROWS = Shared.Height;
        public const uint FPS = Shared.FramesPerSecond;
        public const int NUM_ALLOCATOR_SAMPLES = 10;
        private const long TicksPerFrame = 10_000_000 / FPS; // 100 ns units

        private readonly object _lock = new();
        private readonly MediaSource _source;
        private IComObject<IMFMediaEventQueue>? _queue;
        private IComObject<IMFStreamDescriptor>? _descriptor;
        private IComObject<IMFVideoSampleAllocatorEx>? _allocator;
        private _MF_STREAM_STATE _state;
        private Guid _format;
        private FrameSource _frames = new();
        private long _lastSampleTime;

        public MediaStream(MediaSource source, uint index)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(source);
                _source = source;

                SetGUID(MFConstants.MF_DEVICESTREAM_STREAM_CATEGORY, KSMedia.PINNAME_VIDEO_CAPTURE).ThrowOnError();
                SetUINT32(MFConstants.MF_DEVICESTREAM_STREAM_ID, index).ThrowOnError();
                SetUINT32(MFConstants.MF_DEVICESTREAM_FRAMESERVER_SHARED, 1).ThrowOnError();
                SetUINT32(MFConstants.MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, (uint)_MFFrameSourceTypes.MFFrameSourceTypes_Color).ThrowOnError();

                Functions.MFCreateEventQueue(out var queue).ThrowOnError();
                _queue = new ComObject<IMFMediaEventQueue>(queue);

                // set 1 here to force RGB32 only
                var mediaTypes = new IMFMediaType[2];

                // RGB
                Functions.MFCreateMediaType(out var rgbType).ThrowOnError();
                rgbType.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
                rgbType.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_RGB32).ThrowOnError();
                rgbType.SetSize(MFConstants.MF_MT_FRAME_SIZE, NUM_IMAGE_COLS, NUM_IMAGE_ROWS);
                rgbType.SetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, NUM_IMAGE_COLS * 4).ThrowOnError();
                rgbType.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
                rgbType.SetUINT32(MFConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1).ThrowOnError();
                rgbType.SetRatio(MFConstants.MF_MT_FRAME_RATE, FPS, 1);
                var bitrate = NUM_IMAGE_COLS * 4 * NUM_IMAGE_ROWS * 8 * FPS;
                rgbType.SetUINT32(MFConstants.MF_MT_AVG_BITRATE, bitrate).ThrowOnError();
                rgbType.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
                mediaTypes[0] = rgbType;

                // NV12
                if (mediaTypes.Length > 1)
                {
                    Functions.MFCreateMediaType(out var nv12Type).ThrowOnError();
                    nv12Type.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
                    nv12Type.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
                    nv12Type.SetSize(MFConstants.MF_MT_FRAME_SIZE, NUM_IMAGE_COLS, NUM_IMAGE_ROWS);
                    nv12Type.SetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, NUM_IMAGE_COLS).ThrowOnError(); // Y-plane pitch
                    nv12Type.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
                    nv12Type.SetUINT32(MFConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1).ThrowOnError();
                    nv12Type.SetRatio(MFConstants.MF_MT_FRAME_RATE, FPS, 1);
                    bitrate = NUM_IMAGE_COLS * 3 * NUM_IMAGE_ROWS * 8 * FPS / 2;
                    nv12Type.SetUINT32(MFConstants.MF_MT_AVG_BITRATE, bitrate).ThrowOnError();
                    nv12Type.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
                    mediaTypes[1] = nv12Type;
                }

                Functions.MFCreateStreamDescriptor(index, mediaTypes.Length, mediaTypes, out var descriptor).ThrowOnError();
                descriptor.GetMediaTypeHandler(out var handler).ThrowOnError();
                handler.SetCurrentMediaType(mediaTypes[0]).ThrowOnError();
                _descriptor = new ComObject<IMFStreamDescriptor>(descriptor);
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT Start(IMFMediaType? type)
        {
            var queue = _queue;
            var allocator = _allocator;
            if (queue == null || allocator == null)
            {
                EventProvider.LogInfo($"MF_E_SHUTDOWN");
                return HRESULTS.MF_E_SHUTDOWN;
            }

            if (type != null)
            {
                allocator.Object.InitializeSampleAllocator(NUM_ALLOCATOR_SAMPLES, type).ThrowOnError();

                type.GetGUID(MFConstants.MF_MT_SUBTYPE, out _format).ThrowOnError();
                EventProvider.LogInfo("Format: " + _format.GetMFName());
            }

            queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStarted, Guid.Empty, HRESULTS.S_OK, null).ThrowOnError();
            _state = _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING;
            _frames?.Start(); // the engine opens the camera while a stream runs
            EventProvider.LogInfo("Started");
            return HRESULTS.S_OK;
        }

        public HRESULT Stop()
        {
            var queue = _queue;
            var allocator = _allocator;
            if (queue == null || allocator == null)
            {
                EventProvider.LogInfo($"MF_E_SHUTDOWN");
                return HRESULTS.MF_E_SHUTDOWN;
            }

            allocator.Object.UninitializeSampleAllocator();
            queue.Object.QueueEventParamVar((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStopped, Guid.Empty, HRESULTS.S_OK, null).ThrowOnError();
            _state = _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED;
            _frames?.Stop(); // the engine closes the camera when the last stream stops
            return HRESULTS.S_OK;
        }

        public static MFSampleAllocatorUsage GetAllocatorUsage() => MFSampleAllocatorUsage.MFSampleAllocatorUsage_UsesProvidedAllocator;
        public HRESULT SetAllocator(object allocator)
        {
            if (allocator == null)
            {
                EventProvider.LogInfo($"E_POINTER");
                return HRESULTS.E_POINTER;
            }

            if (allocator is not IMFVideoSampleAllocatorEx aex)
            {
                EventProvider.LogInfo($"E_NOINTERFACE");
                return HRESULTS.E_NOINTERFACE;
            }

            _allocator = new ComObject<IMFVideoSampleAllocatorEx>(aex);
            return HRESULTS.S_OK;
        }

        public HRESULT Set3DManager(object manager)
        {
            // CPU only: the allocator keeps system-memory samples (VCamNetSample README: comment out
            // SetDirectXManager to force CPU usage). Our frames are small and already in memory.
            EventProvider.LogInfo($"manager:{manager} (ignored, CPU samples)");
            return _allocator == null ? HRESULTS.E_POINTER : HRESULTS.S_OK;
        }

        public HRESULT GetEvent(uint flags, out IMFMediaEvent evt)
        {
            EventProvider.LogInfo($"flags:{flags}");
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        evt = null!;
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    var hr = queue.Object.GetEvent(flags, out evt);
                    EventProvider.LogInfo($" => {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT BeginGetEvent(IMFAsyncCallback callback, object state)
        {
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    return queue.Object.BeginGetEvent(callback, state);
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT EndGetEvent(IMFAsyncResult result, out IMFMediaEvent evt)
        {
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        evt = null!;
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    return queue.Object.EndGetEvent(result, out evt);
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT QueueEvent(uint type, Guid extendedType, HRESULT hrStatus, PROPVARIANT value)
        {
            EventProvider.LogInfo($"type:{type} value:{value}");
            try
            {
                lock (_lock)
                {
                    var queue = _queue;
                    if (queue == null)
                    {
                        EventProvider.LogInfo($"MF_E_SHUTDOWN");
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    var hr = queue.Object.QueueEventParamVar(type, extendedType, hrStatus, value);
                    EventProvider.LogInfo($" => {hr}");
                    return hr;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT GetMediaSource(out IMFMediaSource mediaSource)
        {
            EventProvider.LogInfo();
            lock (_lock)
            {
                var source = _source;
                if (source == null)
                {
                    mediaSource = null!;
                    return HRESULTS.MF_E_SHUTDOWN;
                }

                mediaSource = source;
                return HRESULTS.S_OK;
            }
        }

        public HRESULT GetStreamDescriptor(out IMFStreamDescriptor streamDescriptor)
        {
            EventProvider.LogInfo();
            try
            {
                lock (_lock)
                {
                    var descriptor = _descriptor;
                    if (descriptor == null)
                    {
                        streamDescriptor = null!;
                        return HRESULTS.MF_E_SHUTDOWN;
                    }

                    streamDescriptor = descriptor.Object;
                    return HRESULTS.S_OK;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT RequestSample(object token)
        {
            try
            {
                // pace to the advertised rate: the source is live and cheap, so a consumer that asks
                // again at once would otherwise get frames as fast as it can pull them
                var wait = (Volatile.Read(ref _lastSampleTime) + TicksPerFrame - Functions.MFGetSystemTime()) / 10_000;
                if (wait > 0)
                {
                    Thread.Sleep((int)Math.Min(wait, 1000 / FPS));
                }

                lock (_lock)
                {
                    var queue = _queue;
                    var allocator = _allocator;
                    var frames = _frames;
                    if (allocator == null || queue == null || frames == null)
                        return HRESULTS.MF_E_SHUTDOWN;

                    allocator.Object.AllocateSample(out var sample).ThrowOnError();

                    using (var outputSample = new ComObject<IMFSample>(sample))
                    {
                        var now = Functions.MFGetSystemTime();
                        Volatile.Write(ref _lastSampleTime, now);
                        sample.SetSampleTime(now).ThrowOnError();
                        sample.SetSampleDuration(TicksPerFrame).ThrowOnError();

                        frames.WriteSample(outputSample, _format);
                        if (token != null)
                        {
                            outputSample.Object.SetUnknown(MFConstants.MFSampleExtension_Token, token).ThrowOnError();
                        }

                        queue.Object.QueueEventParamUnk((uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEMediaSample, Guid.Empty, HRESULTS.S_OK, outputSample.Object).ThrowOnError();
                    }

                    // the sample does this too: otherwise the allocator gets full too early
                    if (frames.FrameCount % (NUM_ALLOCATOR_SAMPLES / 2) == 0)
                    {
                        GC.Collect();
                    }
                    return HRESULTS.S_OK;
                }
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT SetStreamState(_MF_STREAM_STATE value)
        {
            EventProvider.LogInfo($"value:{value}");
            try
            {
                if (_state != value)
                {
                    switch (value)
                    {
                        case _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED:
                            return Stop();

                        case _MF_STREAM_STATE.MF_STREAM_STATE_PAUSED:
                            if (_state != _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING)
                            {
                                EventProvider.LogInfo($"MF_E_INVALID_STATE_TRANSITION");
                                return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
                            }

                            _state = value;
                            break;

                        case _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING:
                            return Start(null);

                        default:
                            EventProvider.LogInfo($"MF_E_INVALID_STATE_TRANSITION");
                            return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
                    }
                }
                return HRESULTS.S_OK;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT GetStreamState(out _MF_STREAM_STATE value)
        {
            value = _state;
            EventProvider.LogInfo($"value:{value}");
            return HRESULTS.S_OK;
        }

        public HRESULT KsProperty(ref KSIDENTIFIER roperty, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
        {
            EventProvider.LogInfo($"Property:{roperty.GetMFName()} PropertyLength:{propertyLength} DataLength:{dataLength}");
            bytesReturned = 0;
            return HRESULT.FromWin32(Utilities.Constants.ERROR_SET_NOT_FOUND);
        }

        public HRESULT KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
        {
            EventProvider.LogInfo($"Method:{method.GetMFName()} PropertyLength:{methodLength} DataLength:{dataLength}");
            bytesReturned = 0;
            return HRESULT.FromWin32(Utilities.Constants.ERROR_SET_NOT_FOUND);
        }

        public HRESULT KsEvent(ref KSIDENTIFIER evt, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
        {
            EventProvider.LogInfo($"Event:{evt.GetMFName()} PropertyLength:{eventLength} DataLength:{dataLength}");
            bytesReturned = 0;
            return HRESULT.FromWin32(Utilities.Constants.ERROR_SET_NOT_FOUND);
        }

        protected override void Dispose(bool disposing)
        {
            EventProvider.LogInfo();
            Interlocked.Exchange(ref _descriptor!, null)?.Dispose();
            Interlocked.Exchange(ref _queue!, null)?.Dispose();
            Interlocked.Exchange(ref _allocator!, null)?.Dispose();
            Interlocked.Exchange(ref _frames!, null)?.Dispose();
            base.Dispose(disposing);
        }
    }
}
