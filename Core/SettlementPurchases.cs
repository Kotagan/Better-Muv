namespace BetterMuv.Core;

public sealed class SettlementPurchases
{
    public DailySettlementPurchases Daily { get; set; } = new();
    public EquipmentSettlementPurchases Equipment { get; set; } = new();
    public ExcavationSettlementPurchases Excavation { get; set; } = new();
    public ArtifactorSettlementPurchases Artifactor { get; set; } = new();

    public bool HasAnyPurchase() =>
        Daily.HasAny() || Equipment.HasAny() || Excavation.HasAny() || Artifactor.HasAny();

    public void ResetAllToZero()
    {
        Daily.Reset();
        Equipment.Reset();
        Excavation.Reset();
        Artifactor.Reset();
    }

    public void ResetCategory(string category)
    {
        switch (category)
        {
            case "daily": Daily.Reset(); break;
            case "equipment": Equipment.Reset(); break;
            case "excavation": Excavation.Reset(); break;
            case "artifactor": Artifactor.Reset(); break;
            default: throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }
    }
}

public sealed class DailySettlementPurchases
{
    public int[] SkillBook1 { get; set; } = new int[6];
    public int[] SkillBook2 { get; set; } = new int[6];
    public int[] Disk { get; set; } = new int[6];
    public int[] Unit { get; set; } = new int[6];

    public bool HasAny() =>
        HasNonZero(SkillBook1) || HasNonZero(SkillBook2) || HasNonZero(Disk) || HasNonZero(Unit);

    public void Reset()
    {
        Clear(SkillBook1);
        Clear(SkillBook2);
        Clear(Disk);
        Clear(Unit);
    }

    public int[] GetSubcategory(string key) => key switch
    {
        "skillBook1" => SkillBook1,
        "skillBook2" => SkillBook2,
        "disk" => Disk,
        "unit" => Unit,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    private static bool HasNonZero(int[] values) => values.Any(v => v != 0);
    private static void Clear(int[] values) => Array.Fill(values, 0);
}

public sealed class EquipmentSettlementPurchases
{
    public int[] Physics { get; set; } = new int[4];
    public int[] En { get; set; } = new int[4];
    public int[] Agility { get; set; } = new int[4];

    public bool HasAny() => HasNonZero(Physics) || HasNonZero(En) || HasNonZero(Agility);

    public void Reset()
    {
        Clear(Physics);
        Clear(En);
        Clear(Agility);
    }

    public int[] GetSubcategory(string key) => key switch
    {
        "physics" => Physics,
        "en" => En,
        "agility" => Agility,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    private static bool HasNonZero(int[] values) => values.Any(v => v != 0);
    private static void Clear(int[] values) => Array.Fill(values, 0);
}

public sealed class ExcavationSettlementPurchases
{
    public int Funds { get; set; }
    public int Training { get; set; }
    public int Technical { get; set; }

    public bool HasAny() => Funds != 0 || Training != 0 || Technical != 0;

    public void Reset()
    {
        Funds = 0;
        Training = 0;
        Technical = 0;
    }

    public int[] ToSlots() => [Funds, Training, Technical];

    public void FromSlots(int[] slots)
    {
        Funds = slots.ElementAtOrDefault(0);
        Training = slots.ElementAtOrDefault(1);
        Technical = slots.ElementAtOrDefault(2);
    }
}

public sealed class ArtifactorSettlementPurchases
{
    public int[] Physics { get; set; } = new int[5];
    public int[] En { get; set; } = new int[5];
    public int[] Agility { get; set; } = new int[5];

    public bool HasAny() => HasNonZero(Physics) || HasNonZero(En) || HasNonZero(Agility);

    public void Reset()
    {
        Clear(Physics);
        Clear(En);
        Clear(Agility);
    }

    public int[] GetSubcategory(string key) => key switch
    {
        "physics" => Physics,
        "en" => En,
        "agility" => Agility,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    private static bool HasNonZero(int[] values) => values.Any(v => v != 0);
    private static void Clear(int[] values) => Array.Fill(values, 0);
}
