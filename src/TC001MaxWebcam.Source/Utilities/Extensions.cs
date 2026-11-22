// Adapted from VCamNetSample (https://github.com/smourier/VCamNetSample), VCamNetSampleSource/Utilities/Extensions.cs.
// Copyright (c) 2024-2026 Simon Mourier. MIT License, see THIRD_PARTY_NOTICES.txt.
// Changes: namespace; only the GUID-name helpers (the HSL test-pattern colours are gone).

using System;
using DirectN;

namespace TC001MaxWebcam.Utilities
{
    internal static class Extensions
    {
        public static string GetMFName(this Guid guid) => typeof(MFConstants).GetGuidName(guid);
        public static string GetMFName(this KSIDENTIFIER id) => typeof(MFConstants).GetGuidName(id.__union_0.__field_0.Set) + " " + id.__union_0.__field_0.Id;
    }
}
