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
    private readonly TextBlock[] _shopItemNameBlocks = new TextBlock[6];
    private readonly Border[] _shopItemCards = new Border[6];

    private void InitializeShopPanel()
    {
        AutomationConfig config = ConfigStore.Load();
        _shopPurchases = config.SettlementPurchases;
        BuildShopCategoryButtons();
        BuildShopItemCards();
        SelectShopCategory("daily");
    }

    private void ShowShopButton_Click(object sender, RoutedEventArgs e)
    {
        SetNavVisibility(shop: true);
        HomeNavButton.Background = Brushes.Transparent;
        ShopNavButton.Background = new SolidColorBrush(Color.FromRgb(48, 57, 70));
        PriorityNavButton.Background = Brushes.Transparent;
        HotkeyNavButton.Background = Brushes.Transparent;
        SettingsNavButton.Background = Brushes.Transparent;
    }

    private void SetNavVisibility(
        bool home = false, bool shop = false, bool priority = false, bool hotkey = false, bool settings = false)
    {
        HomePanel.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        ShopPanel.Visibility = shop ? Visibility.Visible : Visibility.Collapsed;
        PriorityPanel.Visibility = priority ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPanel.Visibility = hotkey ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildShopCategoryButtons()
    {
        ShopCategoryPanel.Children.Clear();
        foreach (string key in SettlementShopCatalog.CategoryKeys)
        {
            var button = new Button
            {
                Content = SettlementShopCatalog.CategoryDisplayName(key),
                Tag = key,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(215, 220, 227)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 15
            };
            button.Click += (_, _) =>
            {
                PersistShopPurchases(quiet: true);
                SelectShopCategory(key);
            };
            ShopCategoryPanel.Children.Add(button);
        }
    }

    private void BuildShopItemCards()
    {
        ShopItemGrid.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            int row = i / 2;
            int column = i % 2;
            var nameBlock = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var quantityBox = new TextBox
            {
                Width = 88,
                Height = 32,
                FontSize = 15,
                Background = new SolidColorBrush(Color.FromRgb(23, 28, 35)),
                Foreground = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 69, 82)),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = "0"
            };
            quantityBox.LostKeyboardFocus += (_, _) => PersistShopPurchases(quiet: true);
            quantityBox.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                    PersistShopPurchases(quiet: true);
            };
            var hint = new TextBlock
            {
                Text = "-1 全买 / 0 不买",
                Foreground = new SolidColorBrush(Color.FromRgb(158, 167, 179)),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 42, 52)),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(6),
                Padding = new Thickness(14),
                Child = new StackPanel
                {
                    Children = { nameBlock, quantityBox, hint }
                }
            };
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            ShopItemGrid.Children.Add(card);
            _shopItemCards[i] = card;
            _shopItemNameBlocks[i] = nameBlock;
            _shopQuantityBoxes[i] = quantityBox;
        }
    }

    private void SelectShopCategory(string category)
    {
        _shopCategory = category;
        HighlightShopCategoryButtons();
        IReadOnlyList<(string Key, string Name)> subs = SettlementShopCatalog.Subcategories(category);
        ShopSubcategoryPanel.Children.Clear();
        if (subs.Count == 0)
        {
            _shopSubcategory = null;
            ShopSubcategoryPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShopSubcategoryPanel.Visibility = Visibility.Visible;
            _shopSubcategory = subs[0].Key;
            foreach ((string key, string name) in subs)
            {
                var button = new Button
                {
                    Content = name,
                    Tag = key,
                    Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(14, 8, 14, 8),
                    Background = key == _shopSubcategory
                        ? new SolidColorBrush(Color.FromRgb(33, 150, 232))
                        : new SolidColorBrush(Color.FromRgb(52, 60, 72)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                button.Click += (_, _) =>
                {
                    PersistShopPurchases(quiet: true);
                    _shopSubcategory = key;
                    foreach (Button sibling in ShopSubcategoryPanel.Children.OfType<Button>())
                    {
                        sibling.Background = Equals(sibling.Tag, key)
                            ? new SolidColorBrush(Color.FromRgb(33, 150, 232))
                            : new SolidColorBrush(Color.FromRgb(52, 60, 72));
                    }
                    LoadShopItemValues();
                };
                ShopSubcategoryPanel.Children.Add(button);
            }
        }

        LoadShopItemValues();
    }

    private void HighlightShopCategoryButtons()
    {
        foreach (Button button in ShopCategoryPanel.Children.OfType<Button>())
        {
            bool selected = Equals(button.Tag, _shopCategory);
            button.Background = selected
                ? new SolidColorBrush(Color.FromRgb(48, 57, 70))
                : Brushes.Transparent;
            button.Foreground = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(215, 220, 227));
        }
    }

    private void LoadShopItemValues()
    {
        int[] quantities = SettlementShopCatalog.GetQuantities(_shopPurchases, _shopCategory, _shopSubcategory);
        IReadOnlyList<string> names = SettlementShopCatalog.ItemNames(_shopCategory, _shopSubcategory);
        int active = SettlementShopCatalog.ActiveSlotCount(_shopCategory);
        for (int i = 0; i < 6; i++)
        {
            bool enabled = i < active && !string.IsNullOrWhiteSpace(names[i]);
            _shopItemNameBlocks[i].Text = enabled ? names[i] : "—";
            _shopQuantityBoxes[i].Text = enabled ? quantities[i].ToString() : "0";
            _shopQuantityBoxes[i].IsEnabled = enabled;
            _shopItemCards[i].Opacity = enabled ? 1.0 : 0.35;
        }
    }

    private void CommitShopItemEdits()
    {
        var values = new int[6];
        int active = SettlementShopCatalog.ActiveSlotCount(_shopCategory);
        for (int i = 0; i < 6; i++)
        {
            if (i >= active || !_shopQuantityBoxes[i].IsEnabled)
            {
                values[i] = 0;
                continue;
            }

            if (!int.TryParse(_shopQuantityBoxes[i].Text.Trim(), out int quantity))
                quantity = 0;
            values[i] = quantity;
        }

        SettlementShopCatalog.SetQuantities(_shopPurchases, _shopCategory, _shopSubcategory, values);
    }

    private void PersistShopPurchases(bool quiet = false)
    {
        CommitShopItemEdits();
        AutomationConfig config = ConfigStore.Load();
        config.SettlementPurchases = _shopPurchases;
        ConfigStore.Save(config);
        if (!quiet)
            AppendLog("商店购买配置已写入：" + ConfigStore.UserConfigPath);
    }

    private void ResetShopPurchases_Click(object sender, RoutedEventArgs e)
    {
        _shopPurchases.ResetAllToZero();
        LoadShopItemValues();
        PersistShopPurchases();
        AppendLog("商店购买配置已全部置 0。");
    }
}
