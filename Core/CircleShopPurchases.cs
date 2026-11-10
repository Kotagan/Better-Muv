namespace BetterMuv.Core;

/// <summary>社团商店（交換所左侧「サークル」）购买配置。0=不买，正数=次数，-1=全买。</summary>
public sealed class CircleShopPurchases
{
    /// <summary>与 <see cref="CircleShopCatalog.SlotCount"/> 对齐的格子数量。</summary>
    public int[] Slots { get; set; } = new int[CircleShopCatalog.SlotCount];

    public bool HasAny() => Slots.Any(v => v != 0);

    public void Reset() => Array.Fill(Slots, 0);

    public int[] GetSlots()
    {
        EnsureLength();
        return Slots;
    }

    public void SetSlots(IReadOnlyList<int> values)
    {
        EnsureLength();
        for (int i = 0; i < Slots.Length; i++)
            Slots[i] = i < values.Count ? values[i] : 0;
    }

    private void EnsureLength()
    {
        if (Slots.Length == CircleShopCatalog.SlotCount)
            return;
        var next = new int[CircleShopCatalog.SlotCount];
        Array.Copy(Slots, next, Math.Min(Slots.Length, next.Length));
        Slots = next;
    }
}

public static class CircleShopCatalog
{
    public const int SlotCount = 8;

    /// <summary>交換所「サークル」页左上→右下 8 格显示名（与点击格对齐）。</summary>
    public static readonly string[] ItemNames =
    [
        "社团抽奖券",
        "资金（12小时）",
        "橙卡",
        "补给物资",
        "技术报告",
        "加速卡带",
        "能量卡带",
        "其他"
    ];

    public static string FormatPlan(IReadOnlyList<int> quantities)
    {
        var parts = new List<string>();
        for (int i = 0; i < quantities.Count && i < ItemNames.Length; i++)
        {
            int q = quantities[i];
            if (q == 0)
                continue;
            parts.Add(q < 0 ? $"{ItemNames[i]}=全买" : $"{ItemNames[i]}×{q}");
        }
        return parts.Count == 0 ? "（无）" : string.Join("，", parts);
    }

    public static bool IsPageBuyAll(CircleShopPurchases purchases) =>
        purchases.GetSlots().All(q => q == -1);

    public static void SetPageBuyAll(CircleShopPurchases purchases, bool buyAll)
    {
        int[] slots = purchases.GetSlots();
        for (int i = 0; i < slots.Length; i++)
            slots[i] = buyAll ? -1 : 0;
        purchases.SetSlots(slots);
    }
}
