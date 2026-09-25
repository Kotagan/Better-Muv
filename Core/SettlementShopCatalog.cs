namespace BetterMuv.Core;

public static class SettlementShopCatalog
{
    public static readonly string[] CategoryKeys = ["daily", "equipment", "excavation", "artifactor"];

    public static string CategoryDisplayName(string key) => key switch
    {
        "daily" => "日常",
        "equipment" => "装备",
        "excavation" => "挖掘",
        "artifactor" => "Artifactor",
        _ => key
    };

    public static string SubcategoryDisplayName(string category, string? subcategory)
    {
        if (string.IsNullOrWhiteSpace(subcategory))
            return CategoryDisplayName(category);
        foreach ((string key, string name) in Subcategories(category))
        {
            if (key.Equals(subcategory, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return subcategory;
    }

    public static string FormatSlotPlan(string category, string? subcategory, IReadOnlyList<int> quantities)
    {
        IReadOnlyList<string> names = ItemNames(category, subcategory);
        var parts = new List<string>();
        for (int i = 0; i < quantities.Count; i++)
        {
            int q = quantities[i];
            if (q == 0)
                continue;
            string name = i < names.Count && !string.IsNullOrWhiteSpace(names[i])
                ? names[i]
                : $"第{i + 1}格";
            parts.Add(q < 0 ? $"{name}=全买" : $"{name}×{q}");
        }
        return parts.Count == 0 ? "（无）" : string.Join("，", parts);
    }

    public static IReadOnlyList<(string Key, string Name)> Subcategories(string category) => category switch
    {
        "daily" =>
        [
            ("skillBook1", "技能书 I"),
            ("skillBook2", "技能书 II"),
            ("disk", "磁带"),
            ("unit", "升级单元")
        ],
        "equipment" =>
        [
            ("physics", "物理"),
            ("en", "EN"),
            ("agility", "敏捷")
        ],
        "excavation" => [],
        "artifactor" =>
        [
            ("physics", "物理"),
            ("en", "EN"),
            ("agility", "敏捷")
        ],
        _ => []
    };

    public static IReadOnlyList<string> ItemNames(string category, string? subcategory) => category switch
    {
        "daily" => subcategory switch
        {
            "skillBook1" or "skillBook2" => ["火", "水", "木", "土", "光", "暗"],
            "disk" => ["更新磁盘", "", "", "", "", ""],
            "unit" => ["更新单元", "", "", "", "", ""],
            _ => ["", "", "", "", "", ""]
        },
        "equipment" => ["传感器", "执行器", "电池", "引擎", "", ""],
        "excavation" => ["资金", "训练报告", "技术报告", "", "", ""],
        "artifactor" => ["HP", "攻击", "防御", "速度", "会心", ""],
        _ => ["", "", "", "", "", ""]
    };

    public static int ActiveSlotCount(string category) => category switch
    {
        "daily" => 6,
        "equipment" => 4,
        "excavation" => 3,
        "artifactor" => 5,
        _ => 0
    };

    public static int[] GetQuantities(SettlementPurchases purchases, string category, string? subcategory)
    {
        return category switch
        {
            "daily" => Pad(purchases.Daily.GetSubcategory(subcategory ?? "skillBook1"), 6),
            "equipment" => Pad(purchases.Equipment.GetSubcategory(subcategory ?? "physics"), 6),
            "excavation" => Pad(purchases.Excavation.ToSlots(), 6),
            "artifactor" => Pad(purchases.Artifactor.GetSubcategory(subcategory ?? "physics"), 6),
            _ => new int[6]
        };
    }

    public static void SetQuantities(
        SettlementPurchases purchases, string category, string? subcategory, int[] values)
    {
        switch (category)
        {
            case "daily":
                CopyInto(purchases.Daily.GetSubcategory(subcategory ?? "skillBook1"), values);
                break;
            case "equipment":
                CopyInto(purchases.Equipment.GetSubcategory(subcategory ?? "physics"), values);
                break;
            case "excavation":
                purchases.Excavation.FromSlots(values);
                break;
            case "artifactor":
                CopyInto(purchases.Artifactor.GetSubcategory(subcategory ?? "physics"), values);
                break;
        }
    }

    /// <summary>将指定栏位购买量设为 0（关闭）。返回是否发生了变更。</summary>
    public static bool DisableSlot(
        SettlementPurchases purchases, string category, string? subcategory, int slotIndex)
    {
        int active = ActiveSlotCount(category);
        if (slotIndex < 0 || slotIndex >= active)
            return false;

        int[] quantities = GetQuantities(purchases, category, subcategory);
        if (quantities[slotIndex] == 0)
            return false;

        quantities[slotIndex] = 0;
        SetQuantities(purchases, category, subcategory, quantities);
        return true;
    }

    /// <summary>将指定小类（或无小类的当前页）全部栏位设为全买(-1)或清零。</summary>
    public static void SetPageBuyAll(
        SettlementPurchases purchases, string category, string? subcategory, bool buyAll)
    {
        int active = ActiveSlotCount(category);
        IReadOnlyList<string> names = ItemNames(category, subcategory);
        var values = new int[6];
        int fill = buyAll ? -1 : 0;
        for (int i = 0; i < active; i++)
            values[i] = string.IsNullOrWhiteSpace(names[i]) ? 0 : fill;
        SetQuantities(purchases, category, subcategory, values);
    }

    /// <summary>当前小类页是否全部为全买。</summary>
    public static bool IsPageBuyAll(SettlementPurchases purchases, string category, string? subcategory)
    {
        int[] quantities = GetQuantities(purchases, category, subcategory);
        IReadOnlyList<string> names = ItemNames(category, subcategory);
        int active = ActiveSlotCount(category);
        bool any = false;
        for (int i = 0; i < active; i++)
        {
            if (string.IsNullOrWhiteSpace(names[i]))
                continue;
            any = true;
            if (quantities[i] != -1)
                return false;
        }
        return any;
    }

    /// <summary>将该大类下所有小类全部设为全买或清零。</summary>
    public static void SetCategoryBuyAll(SettlementPurchases purchases, string category, bool buyAll)
    {
        IReadOnlyList<(string Key, string Name)> subs = Subcategories(category);
        if (subs.Count == 0)
        {
            SetPageBuyAll(purchases, category, null, buyAll);
            return;
        }

        foreach ((string key, _) in subs)
            SetPageBuyAll(purchases, category, key, buyAll);
    }

    /// <summary>该大类下所有小类是否全部为全买。</summary>
    public static bool IsCategoryBuyAll(SettlementPurchases purchases, string category)
    {
        IReadOnlyList<(string Key, string Name)> subs = Subcategories(category);
        if (subs.Count == 0)
            return IsPageBuyAll(purchases, category, null);

        foreach ((string key, _) in subs)
        {
            if (!IsPageBuyAll(purchases, category, key))
                return false;
        }
        return true;
    }

    private static int[] Pad(int[] source, int length)
    {
        var result = new int[length];
        Array.Copy(source, result, Math.Min(source.Length, length));
        return result;
    }

    private static void CopyInto(int[] target, int[] source)
    {
        for (int i = 0; i < target.Length && i < source.Length; i++)
            target[i] = source[i];
    }
}
