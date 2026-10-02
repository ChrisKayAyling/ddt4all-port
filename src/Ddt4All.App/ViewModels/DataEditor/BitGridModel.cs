namespace Ddt4All.App.ViewModels;

/// <summary>A run of bits of one item inside a single byte row (columns 0..7 = bit 7..0, MSB first).</summary>
public readonly record struct BitSpan(int Item, int Byte, int Col, int Count);

/// <summary>Input of the bit grid: one placed data item.</summary>
public readonly record struct BitPlacement(string Name, int FirstByte, int BitOffset, int Bits, bool Little);

/// <summary>Pure layout model of the bit/byte viewer: which bits of which frame bytes each item occupies.</summary>
public sealed class BitGridModel
{
    public const int Overlap = -2;
    public const int Free = -1;

    public int ByteCount { get; }
    /// <summary>byte*8+col -> item index, <see cref="Free"/> or <see cref="Overlap"/>.</summary>
    public int[] Cells { get; }
    public IReadOnlyList<BitSpan> Spans { get; }
    public IReadOnlyList<string> Names { get; }
    /// <summary>Per byte caption ("1", "2"...), optionally followed by the template value.</summary>
    public IReadOnlyList<string> ByteValues { get; }
    public bool HasOverlap { get; }

    public static BitGridModel Empty { get; } = new(Array.Empty<BitPlacement>(), 0, Array.Empty<string>());

    private static List<int> CellsOf(BitPlacement it)
    {
        var cells = new List<int>(Math.Min(Math.Max(it.Bits, 0), 4096));
        if (it.FirstByte < 1 || it.Bits <= 0) return cells;
        if (!it.Little)
        {
            int start = (it.FirstByte - 1) * 8 + it.BitOffset;
            for (int i = 0; i < it.Bits && cells.Count < 4096; i++) cells.Add(start + i);
            return cells;
        }

        // little endian: the field ends BitOffset bits before the end of the first byte and continues in the following bytes
        int baseBit = (it.FirstByte - 1) * 8;
        int last = 8 - Math.Min(it.BitOffset, 7);
        int first = Math.Max(0, last - it.Bits);
        for (int c = first; c < last; c++) cells.Add(baseBit + c);
        int remaining = it.Bits - (last - first);
        for (int b = 1; remaining > 0 && cells.Count < 4096; b++)
            for (int c = 0; c < 8 && remaining > 0; c++, remaining--) cells.Add(baseBit + b * 8 + c);
        return cells;
    }

    public BitGridModel(IReadOnlyList<BitPlacement> items, int minBytes, IReadOnlyList<string> byteValues)
    {
        var all = new List<int>[items.Count];
        int maxBit = Math.Max(0, minBytes) * 8;
        for (int i = 0; i < items.Count; i++)
        {
            all[i] = CellsOf(items[i]);
            foreach (int b in all[i]) if (b + 1 > maxBit) maxBit = b + 1;
        }

        int bytes = Math.Min((maxBit + 7) / 8, 512);
        Cells = new int[bytes * 8];
        Array.Fill(Cells, Free);
        var names = new List<string>(items.Count);
        var spans = new List<BitSpan>();
        bool overlap = false;
        for (int idx = 0; idx < items.Count; idx++)
        {
            names.Add(items[idx].Name);
            int runByte = -1, runStart = 0, runCount = 0;
            foreach (int bit in all[idx])
            {
                if (bit >= Cells.Length) continue;
                if (Cells[bit] == Free) Cells[bit] = idx; else { Cells[bit] = Overlap; overlap = true; }
                int b = bit >> 3, c = bit & 7;
                if (b == runByte && c == runStart + runCount) runCount++;
                else
                {
                    if (runCount > 0) spans.Add(new BitSpan(idx, runByte, runStart, runCount));
                    runByte = b; runStart = c; runCount = 1;
                }
            }

            if (runCount > 0) spans.Add(new BitSpan(idx, runByte, runStart, runCount));
        }

        ByteCount = bytes;
        Spans = spans;
        Names = names;
        HasOverlap = overlap;
        var vals = new string[bytes];
        for (int i = 0; i < bytes; i++) vals[i] = i < byteValues.Count ? byteValues[i] : "";
        ByteValues = vals;
    }
}
