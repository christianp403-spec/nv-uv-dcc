namespace NvUv.Dcc.Core;

public sealed class SupportedClockCatalog
{
    private readonly int[] _graphicsBinsMHz;

    public SupportedClockCatalog(IEnumerable<int> graphicsBinsMHz)
    {
        _graphicsBinsMHz = graphicsBinsMHz
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        if (_graphicsBinsMHz.Length == 0)
            throw new ArgumentException("No valid graphics clock bins were supplied.", nameof(graphicsBinsMHz));
    }

    public IReadOnlyList<int> GraphicsBinsMHz => _graphicsBinsMHz;
    public int Count => _graphicsBinsMHz.Length;
    public int MinimumMHz => _graphicsBinsMHz[0];
    public int MaximumMHz => _graphicsBinsMHz[^1];

    public bool Contains(int clockMHz) => Array.BinarySearch(_graphicsBinsMHz, clockMHz) >= 0;

    public int IndexOfNearest(int requestedMHz)
    {
        int snapped = SnapNearest(requestedMHz);
        return Array.BinarySearch(_graphicsBinsMHz, snapped);
    }

    public int At(int index) => _graphicsBinsMHz[Math.Clamp(index, 0, _graphicsBinsMHz.Length - 1)];

    public int StepByBins(int clockMHz, int binDelta) =>
        At(IndexOfNearest(clockMHz) + binDelta);

    public int StepByMHz(int clockMHz, int deltaMHz) =>
        SnapNearest(Math.Clamp(clockMHz + deltaMHz, MinimumMHz, MaximumMHz));

    public int SnapNearest(int requestedMHz)
    {
        int index = Array.BinarySearch(_graphicsBinsMHz, requestedMHz);
        if (index >= 0)
            return _graphicsBinsMHz[index];

        int upper = ~index;
        if (upper <= 0)
            return MinimumMHz;
        if (upper >= _graphicsBinsMHz.Length)
            return MaximumMHz;

        int lowerValue = _graphicsBinsMHz[upper - 1];
        int upperValue = _graphicsBinsMHz[upper];
        return requestedMHz - lowerValue <= upperValue - requestedMHz
            ? lowerValue
            : upperValue;
    }
}
