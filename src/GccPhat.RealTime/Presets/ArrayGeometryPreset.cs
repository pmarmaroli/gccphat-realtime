using System.Collections.Generic;

namespace GccPhat.RealTime.Presets;

public sealed record MicPositionEntry(int PositionIndex, double X, double Y, double Z, int? Channel, bool IncludeInBeam);

public sealed record ArrayGeometryPreset(
    string Name,
    string Layout,
    int MicCount,
    double DiameterCm,
    double SpacingCm,
    bool HasCenterMic,
    string CustomInputMode,
    IReadOnlyList<MicPositionEntry> Positions);
