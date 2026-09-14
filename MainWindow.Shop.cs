using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterMuv.Core;

namespace BetterMuv;

public partial class MainWindow
{
    private SettlementPurchases _shopPurchases = new();
    private string _shopCategory = "daily";
    private string? _shopSubcategory = "skillBook1";
    private readonly TextBox[] _shopQuantityBoxes = new TextBox[6];
    private readonly CheckBox[] _shopBuyAllSwitches = new CheckBox[6];
    private readonly TextBlock[] _shopItemNameBlocks = new TextBlock[6];
    private readonly Border[] _shopItemCards = new Border[6];
    private bool _isLoadingShopValues;

    private void InitializeShopPanel()
    {
        _shopPurchases = ConfigStore.Load().SettlementPurchases;
        BuildShopCategoryButtons();
        BuildShopItemCards();
        SelectShopCategory("daily");
    }

    /// <summary>自动购买可能关闭达到上限的栏位；任务结束后同步单格及两级全买开关。</summary>
    private void ReloadShopPurchases()
    {
        _shopPurchases = ConfigStore.Load().SettlementPurchases;
        LoadShopItemValues();
    }

    private void BuildShopCategoryButtons()
    {
        ShopCategoryPanel.Children.Clear();
        foreach (string key in SettlementShopCatalog.CategoryKeys)
        {
            var button = new Button { Content = SettlementShopCatalog.CategoryDisplayName(key), Tag = key, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 8, 14, 8), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            button.Click += (_, _) => { PersistShopPurchases(true); SelectShopCategory(key); };
            ShopCategoryPanel.Children.Add(button);
        }
    }

    private void BuildShopItemCards()
    {
        ShopItemGrid.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            int slot = i;
            var name = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var buyAllLabel = new TextBlock { Text = "全买", Foreground = new SolidColorBrush(Color.FromRgb(190, 199, 210)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            var buyAll = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var quantity = new TextBox
            {
                Width = 72,
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(4, 2, 4, 2),
                Text = "0",
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            quantity.LostKeyboardFocus += (_, _) => PersistShopPurchases(true);
            buyAll.Checked += (_, _) => UpdateBuyAllState(slot);
            buyAll.Unchecked += (_, _) => UpdateBuyAllState(slot);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(name);
            Grid.SetColumn(buyAllLabel, 1);
            Grid.SetColumn(buyAll, 2);
            Grid.SetColumn(quantity, 3);
            content.Children.Add(buyAllLabel);
            content.Children.Add(buyAll);
            content.Children.Add(quantity);
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(35, 42, 52)), CornerRadius = new CornerRadius(8), Margin = new Thickness(4), Padding = new Thickness(14, 10, 12, 10), Height = 58, Child = content };
            Grid.SetRow(card, slot / 2); Grid.SetColumn(card, slot % 2);
            ShopItemGrid.Children.Add(card);
            _shopQuantityBoxes[slot] = quantity; _shopItemNameBlocks[slot] = name; _shopItemCards[slot] = card;
            _shopBuyAllSwitches[slot] = buyAll;
        }
    }

    private void SelectShopCategory(string category)
    {
        _shopCategory = category;
        foreach (Button button in ShopCategoryPanel.Children.OfType<Button>()) button.Background = Equals(button.Tag, category) ? new SolidColorBrush(Color.FromRgb(59, 66, 78)) : Brushes.Transparent;
        IReadOnlyList<(string Key, string Name)> subs = SettlementShopCatalog.Subcategories(category);
        ShopSubcategoryPanel.Children.Clear();
        _shopSubcategory = subs.Count == 0 ? null : subs[0].Key;
        foreach ((string key, string name) in subs)
        {
            var button = new Button { Content = name, Tag = key, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 5, 8, 5), Background = key == _shopSubcategory ? new SolidColorBrush(Color.FromRgb(59, 66, 78)) : new SolidColorBrush(Color.FromRgb(42, 48, 58)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            button.Click += (_, _) =>
            {
                PersistShopPurchases(true);
                _shopSubcategory = key;
                foreach (Button sibling in ShopSubcategoryPanel.Children.OfType<Button>())
                    sibling.Background = Equals(sibling.Tag, key)
                        ? new SolidColorBrush(Color.FromRgb(59, 66, 78))
                        : new SolidColorBrush(Color.FromRgb(42, 48, 58));
                LoadShopItemValues();
            };
            ShopSubcategoryPanel.Children.Add(button);
        }
        LoadShopItemValues();
        UpdateShopResetButtons();
    }

    private void LoadShopItemValues()
    {
        _isLoadingShopValues = true;
        int[] quantities = SettlementShopCatalog.GetQuantities(_shopPurchases, _shopCategory, _shopSubcategory);
        IReadOnlyList<string> names = SettlementShopCatalog.ItemNames(_shopCategory, _shopSubcategory);
        int active = SettlementShopCatalog.ActiveSlotCount(_shopCategory);
        for (int i = 0; i < 6; i++)
        {
            bool enabled = i < active && !string.IsNullOrWhiteSpace(names[i]);
            _shopItemCards[i].Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                _shopItemNameBlocks[i].Text = "";
                _shopBuyAllSwitches[i].IsChecked = false;
                _shopBuyAllSwitches[i].IsEnabled = false;
                _shopQuantityBoxes[i].Text = "0";
                _shopQuantityBoxes[i].IsEnabled = false;
                continue;
            }

            _shopItemNameBlocks[i].Text = names[i];
            bool buyAll = quantities[i] == -1;
            _shopBuyAllSwitches[i].IsChecked = buyAll;
            _shopQuantityBoxes[i].Text = buyAll ? "0" : quantities[i].ToString();
            _shopQuantityBoxes[i].IsEnabled = !buyAll;
            _shopBuyAllSwitches[i].IsEnabled = true;
            _shopItemCards[i].Opacity = 1;
        }

        ShopSubcategoryBuyAllSwitch.IsChecked =
            SettlementShopCatalog.IsPageBuyAll(_shopPurchases, _shopCategory, _shopSubcategory);
        ShopCategoryBuyAllSwitch.IsChecked =
            SettlementShopCatalog.IsCategoryBuyAll(_shopPurchases, _shopCategory);
        _isLoadingShopValues = false;
    }

    private void PersistShopPurchases(bool quiet = false)
    {
        var values = new int[6];
        int active = SettlementShopCatalog.ActiveSlotCount(_shopCategory);
        for (int i = 0; i < 6; i++) values[i] = i < active
            ? _shopBuyAllSwitches[i].IsChecked == true ? -1 : int.TryParse(_shopQuantityBoxes[i].Text, out int value) ? Math.Max(0, value) : 0
            : 0;
        SettlementShopCatalog.SetQuantities(_shopPurchases, _shopCategory, _shopSubcategory, values);
        AutomationConfig config = ConfigStore.Load(); config.SettlementPurchases = _shopPurchases; ConfigStore.Save(config);
        if (!quiet) AppendLog("商店购买配置已保存。");
    }

    private void UpdateBuyAllState(int slot)
    {
        if (_isLoadingShopValues) return;
        _shopQuantityBoxes[slot].IsEnabled = _shopBuyAllSwitches[slot].IsChecked != true;
        PersistShopPurchases(true);
        _isLoadingShopValues = true;
        ShopSubcategoryBuyAllSwitch.IsChecked =
            SettlementShopCatalog.IsPageBuyAll(_shopPurchases, _shopCategory, _shopSubcategory);
        ShopCategoryBuyAllSwitch.IsChecked =
            SettlementShopCatalog.IsCategoryBuyAll(_shopPurchases, _shopCategory);
        _isLoadingShopValues = false;
    }

    private void ShopCategoryBuyAllSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingShopValues) return;
        bool buyAll = ShopCategoryBuyAllSwitch.IsChecked == true;
        SettlementShopCatalog.SetCategoryBuyAll(_shopPurchases, _shopCategory, buyAll);
        LoadShopItemValues();
        PersistShopPurchases(true);
    }

    private void ShopSubcategoryBuyAllSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingShopValues) return;
        bool buyAll = ShopSubcategoryBuyAllSwitch.IsChecked == true;
        SettlementShopCatalog.SetPageBuyAll(_shopPurchases, _shopCategory, _shopSubcategory, buyAll);
        LoadShopItemValues();
        PersistShopPurchases(true);
    }

    private void UpdateShopResetButtons()
    {
        ResetShopCategoryButton.Content = $"重置{SettlementShopCatalog.CategoryDisplayName(_shopCategory)}购买数量";
    }

    private void ResetCurrentShopCategoryPurchases_Click(object sender, RoutedEventArgs e)
    {
        _shopPurchases.ResetCategory(_shopCategory);
        LoadShopItemValues();
        PersistShopPurchases();
    }

    private void ResetCurrentShopPagePurchases_Click(object sender, RoutedEventArgs e)
    {
        SettlementShopCatalog.SetQuantities(_shopPurchases, _shopCategory, _shopSubcategory, new int[6]);
        LoadShopItemValues();
        PersistShopPurchases();
    }
}
