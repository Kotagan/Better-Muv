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
            "skillBook1" or "skillBook2" => ["攻", "智", "羞", "萌", "谐", "巧"],
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
