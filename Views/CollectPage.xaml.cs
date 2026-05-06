using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using PhotoView.Controls;
using PhotoView.Contracts.Services;
using PhotoView.Dialogs;
using PhotoView.Helpers;
using PhotoView.Models;
using PhotoView.Services;
using PhotoView.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace PhotoView.Views;

public sealed partial class CollectPage : Page
{
    private const double LoadDrawerExpandedWidth = 265;
    private const double LoadDrawerCollapsedWidth = 25;
    private const double InfoDrawerExpandedWidth = 340;
    private const double InfoDrawerCollapsedWidth = 0;
    private const int VisibleThumbnailStartBudgetPerTick = 16;
    private const int VisibleThumbnailPrefetchItemCount = 12;
    private const int DirectionalNavigationRepeatIntervalMs = 180;
    private readonly DispatcherTimer _ratingDebounceTimer;
    private readonly IKeyboardShortcutService _shortcutService;
    private readonly CollectPageThumbnailCoordinator _thumbnailCoordinator;
    private readonly Dictionary<RatingControl, bool> _ratingControlEventMap = new();
    private readonly ISettingsService _settingsService;
    private readonly ShellToolbarService _shellToolbarService;
    private FolderNode? _rightClickedFolderNode;
    private PointerEventHandler? _thumbnailWheelHandler;
    private KeyEventHandler? _thumbnailPreviewKeyDownHandler;
    private Storyboard? _loadDrawerStoryboard;
    private Storyboard? _infoDrawerStoryboard;
    private bool _isUnloaded;
    private bool _isDisposed;
    private bool _isUpdatingZoomSlider;
    private bool _isLoadDrawerPinnedCollapsed;
    private bool _isLoadDrawerTemporarilyExpanded;
    private bool _suppressNextThumbnailDragStart;
    private Button? _shellDeleteButton;
    private SplitButton? _shellFilterSplitButton;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _shellFilterActiveIndicator;
    private CollectSourcePane? _sourcePane;
    private SplitButton? _sourceSplitButton;
    private InfoBadge? _sourceBadge;
    private ContentControl? _sourceLoadIcon;
    private TextBlock? _sourceLoadText;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _sourceLoadActiveIndicator;
    private (ImageFileInfo Image, uint Rating)? _pendingRatingUpdate;
    private int _selectedThumbnailLoadVersion;
    private int _lastDirectionalNavigationDirection;
    private long _lastDirectionalNavigationTick;
    private bool _isUpdatingSelectedImageFromPreview;
    private bool _isUpdatingDualPageZoom;
    private bool _isSyncingPreviewViewportState;
    private ImageFileInfo? _secondaryFocusedThumbnail;
    private PointerEventHandler? _previewWheelHandler;
    private PointerEventHandler? _previewPointerPressedHandler;

    public CollectViewModel ViewModel
    {
        get;
    }

    public ImageViewerViewModel PreviewInfoViewModel
    {
        get;
    }

    public CollectPage()
    {
        ViewModel = App.GetService<CollectViewModel>();
        PreviewInfoViewModel = App.GetService<ImageViewerViewModel>();
        _settingsService = App.GetService<ISettingsService>();
        _shellToolbarService = App.GetService<ShellToolbarService>();
        _shortcutService = App.GetService<IKeyboardShortcutService>();
        
        NavigationCacheMode = NavigationCacheMode.Disabled;
        InitializeComponent();
        DataContext = ViewModel;
        ApplyLocalizedToolTips();
        ActualThemeChanged += CollectPage_ActualThemeChanged;
        UpdateInfoDrawerState(animate: false);
        UpdateZoomValueButtonContent(100d, isFitZoomActive: false);
        RefreshCustomIconForegrounds();
        _thumbnailCoordinator = new CollectPageThumbnailCoordinator(TimeSpan.FromMilliseconds(50));
        _thumbnailCoordinator.VisibleThumbnailLoadTimer.Tick += VisibleThumbnailLoadTimer_Tick;
        _ratingDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _ratingDebounceTimer.Tick += RatingDebounceTimer_Tick;
        ViewModel.Images.CollectionChanged += Images_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Filter.FilterChanged += Filter_FilterChanged;
        PreviewInfoViewModel.RatingUpdated += PreviewInfoViewModel_RatingUpdated;
        PreviewCanvas.ZoomPercentChanged += PreviewCanvas_ZoomPercentChanged;
        LeftPreviewCanvas.ZoomPercentChanged += PreviewCanvas_ZoomPercentChanged;
        RightPreviewCanvas.ZoomPercentChanged += PreviewCanvas_ZoomPercentChanged;
        LeftPreviewCanvas.ViewportStateChanged += PreviewCanvas_ViewportStateChanged;
        RightPreviewCanvas.ViewportStateChanged += PreviewCanvas_ViewportStateChanged;
        _thumbnailWheelHandler = PreviewThumbnailGridView_PointerWheelChanged;
        PreviewThumbnailGridView.AddHandler(UIElement.PointerWheelChangedEvent, _thumbnailWheelHandler, true);
        _previewWheelHandler = PreviewHost_PointerWheelChanged;
        LeftPreviewHost.AddHandler(UIElement.PointerWheelChangedEvent, _previewWheelHandler, true);
        RightPreviewHost.AddHandler(UIElement.PointerWheelChangedEvent, _previewWheelHandler, true);
        _previewPointerPressedHandler = PreviewHost_PointerPressed;
        LeftPreviewHost.AddHandler(UIElement.PointerPressedEvent, _previewPointerPressedHandler, true);
        RightPreviewHost.AddHandler(UIElement.PointerPressedEvent, _previewPointerPressedHandler, true);
        _thumbnailPreviewKeyDownHandler = PreviewThumbnailGridView_PreviewKeyDown;
        PreviewThumbnailGridView.AddHandler(UIElement.PreviewKeyDownEvent, _thumbnailPreviewKeyDownHandler, true);
        Loaded += CollectPage_Loaded;
        Unloaded += CollectPage_Unloaded;
        
        _shortcutService.RegisterPageShortcutHandler("CollectPage", HandleShortcut);
    }

    private void CollectPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        RefreshCustomIconForegrounds();
        UpdateSourceLoadButtonForeground();
    }

    private async void CollectPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
            return;

        _isUnloaded = false;
        _isLoadDrawerPinnedCollapsed = await _settingsService.LoadCollectPageLoadDrawerCollapsedAsync();
        _isLoadDrawerTemporarilyExpanded = false;
        RegisterShellToolbar();
        EnsurePreviewSelectionInitialized();
        RefreshDualPageLayout(forceRebuild: true);
        UpdateSelectedImageUi();
        EnsureLoadDrawerOpenForEmptyPreview(animate: false);
        UpdateLoadDrawerState(animate: false);
        RefreshCustomIconForegrounds();
        QueueVisibleThumbnailLoad("page-loaded");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shortcutService.SetCurrentPage("CollectPage");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _shortcutService.SetCurrentPage("");
    }

    private void CollectPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _thumbnailCoordinator.ResetState();
        
        DisposePageSubscriptions();
    }

    private void DisposePageSubscriptions()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _shellToolbarService.ClearToolbar(this);
        _shellToolbarService.UpdateProgress(false, false, 0d);
        _shellDeleteButton = null;
        _shellFilterSplitButton = null;
        _shellFilterActiveIndicator = null;
        _sourcePane = null;
        _sourceSplitButton = null;
        _sourceBadge = null;
        _sourceLoadIcon = null;
        _sourceLoadText = null;
        _sourceLoadActiveIndicator = null;
        _thumbnailCoordinator.VisibleThumbnailLoadTimer.Tick -= VisibleThumbnailLoadTimer_Tick;
        _ratingDebounceTimer.Tick -= RatingDebounceTimer_Tick;
        ViewModel.Images.CollectionChanged -= Images_CollectionChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Filter.FilterChanged -= Filter_FilterChanged;
        PreviewInfoViewModel.RatingUpdated -= PreviewInfoViewModel_RatingUpdated;
        Bindings.StopTracking();
        PreviewCanvas.ZoomPercentChanged -= PreviewCanvas_ZoomPercentChanged;
        LeftPreviewCanvas.ZoomPercentChanged -= PreviewCanvas_ZoomPercentChanged;
        RightPreviewCanvas.ZoomPercentChanged -= PreviewCanvas_ZoomPercentChanged;
        LeftPreviewCanvas.ViewportStateChanged -= PreviewCanvas_ViewportStateChanged;
        RightPreviewCanvas.ViewportStateChanged -= PreviewCanvas_ViewportStateChanged;
        if (_thumbnailWheelHandler != null)
        {
            PreviewThumbnailGridView.RemoveHandler(UIElement.PointerWheelChangedEvent, _thumbnailWheelHandler);
            _thumbnailWheelHandler = null;
        }
        if (_previewWheelHandler != null)
        {
            LeftPreviewHost.RemoveHandler(UIElement.PointerWheelChangedEvent, _previewWheelHandler);
            RightPreviewHost.RemoveHandler(UIElement.PointerWheelChangedEvent, _previewWheelHandler);
            _previewWheelHandler = null;
        }
        if (_previewPointerPressedHandler != null)
        {
            LeftPreviewHost.RemoveHandler(UIElement.PointerPressedEvent, _previewPointerPressedHandler);
            RightPreviewHost.RemoveHandler(UIElement.PointerPressedEvent, _previewPointerPressedHandler);
            _previewPointerPressedHandler = null;
        }
        if (_thumbnailPreviewKeyDownHandler != null)
        {
            PreviewThumbnailGridView.RemoveHandler(UIElement.PreviewKeyDownEvent, _thumbnailPreviewKeyDownHandler);
            _thumbnailPreviewKeyDownHandler = null;
        }
        Loaded -= CollectPage_Loaded;
        Unloaded -= CollectPage_Unloaded;
    }

    private async void LoadPreview_Click(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.LoadPreviewAsync();
        if (result.AutoCollapseDrawer)
        {
            CollapseLoadDrawer();
        }
        else if (!result.LoadedAnyFiles)
        {
            ExpandLoadDrawer();
        }
        QueueVisibleThumbnailLoad("load-preview");
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PreviewSource source })
        {
            ViewModel.RemoveSource(source);
        }
    }

    private async void PreviewFolderTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FolderNode node)
        {
            await ViewModel.LoadChildrenAsync(node);
        }
    }

    private async void PreviewFolderTreeItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TryGetFolderNode(sender, out var node))
        {
            ViewModel.AddSource(node);
            e.Handled = true;
        }
    }

    private void PreviewFolderTreeItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (TryGetFolderNode(sender, out var node))
        {
            _rightClickedFolderNode = node;
            if (sender is TreeViewItem { Content: Grid grid })
            {
                ShowAttachedFlyoutAtPointer(grid, e);
            }
            e.Handled = true;
        }
    }

    private static void ShowAttachedFlyoutAtPointer(FrameworkElement element, RightTappedRoutedEventArgs args)
    {
        FlyoutBase.GetAttachedFlyout(element)?.ShowAt(element, new FlyoutShowOptions
        {
            Position = args.GetPosition(element)
        });
    }

    private void AddFolderToPreviewSource_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AddSource(_rightClickedFolderNode);
    }

    private void FolderTreeMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        var hasFolderPath = !string.IsNullOrWhiteSpace(_rightClickedFolderNode?.FullPath);
        var isPinned = hasFolderPath && ViewModel.IsFolderPinned(_rightClickedFolderNode);
        var isExternalDeviceNode = IsNodeUnderExternalDevices(_rightClickedFolderNode);

        foreach (var item in flyout.Items)
        {
            if (item is not FrameworkElement element || element.Tag is not string tag)
                continue;

            element.Visibility = tag switch
            {
                "PinFolder" => hasFolderPath && !isPinned ? Visibility.Visible : Visibility.Collapsed,
                "UnpinFolder" => hasFolderPath && isPinned ? Visibility.Visible : Visibility.Collapsed,
                "PinSectionSeparator" => hasFolderPath ? Visibility.Visible : Visibility.Collapsed,
                "RefreshExternalDevices" => isExternalDeviceNode ? Visibility.Visible : Visibility.Collapsed,
                "ExternalDeviceSectionSeparator" => isExternalDeviceNode ? Visibility.Visible : Visibility.Collapsed,
                _ => element.Visibility
            };
        }
    }

    private static bool IsNodeUnderExternalDevices(FolderNode? node)
    {
        while (node != null)
        {
            if (node.NodeType == NodeType.ExternalDevice)
                return true;

            node = node.Parent;
        }

        return false;
    }

    private async void PinFolder_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PinFolderAsync(_rightClickedFolderNode);
    }

    private async void UnpinFolder_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.UnpinFolderAsync(_rightClickedFolderNode);
    }

    private async void RefreshExternalDevices_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshExternalDevicesAsync();
    }

    private static bool TryGetFolderNode(object sender, out FolderNode? node)
    {
        node = null;
        if (sender is TreeViewItem { Content: Grid { Tag: FolderNode taggedNode } })
        {
            node = taggedNode;
            return true;
        }
        if (sender is FrameworkElement { DataContext: FolderNode dataNode })
        {
            node = dataNode;
            return true;
        }
        return false;
    }

    private void PreviewThumbnailGridView_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelectedImageFromPreview)
            return;

        ClearSecondaryThumbnailFocus();

        if (PreviewThumbnailGridView.SelectedItem is ImageFileInfo imageInfo)
        {
            UpdateSelectedImage(imageInfo, syncThumbnailSelection: false);
        }
    }

    private void PreviewThumbnailGridView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_suppressNextThumbnailDragStart)
        {
            _suppressNextThumbnailDragStart = false;
            e.Cancel = true;
            return;
        }

        var dragImages = GetDragImagesFromItems(e.Items).ToList();
        if (dragImages.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var storageFiles = dragImages
            .Select(image => image.ImageFile)
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            .Cast<StorageFile>()
            .ToList();

        if (storageFiles.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties.ApplicationName = "AppDisplayName".GetLocalized();
        e.Data.Properties.Title = storageFiles.Count == 1
            ? storageFiles[0].Name
            : string.Format("Common_FileCount".GetLocalized(), storageFiles.Count);
        e.Data.SetStorageItems(storageFiles);
    }

    private void ApplyLocalizedToolTips()
    {
        ToolTipService.SetToolTip(ThumbnailSizeToggleButton, "CollectPage_Tooltip_CollapseThumbnails".GetLocalized());
        ToolTipService.SetToolTip(InfoDrawerToggleButton, "CollectPage_Tooltip_ImageInfo".GetLocalized());
        ToolTipService.SetToolTip(DualPageToggleButton, "CollectPage_Tooltip_DualPage".GetLocalized());
        ToolTipService.SetToolTip(CompareModeToggleButton, "CollectPage_Tooltip_Compare".GetLocalized());
        ToolTipService.SetToolTip(ContinuousModeToggleButton, "CollectPage_Tooltip_Continuous".GetLocalized());
        ToolTipService.SetToolTip(FlipHorizontalButton, "CollectPage_Tooltip_FlipHorizontal".GetLocalized());
        ToolTipService.SetToolTip(RotatePreviewButton, "CollectPage_Tooltip_Rotate".GetLocalized());
        ToolTipService.SetToolTip(FlipVerticalButton, "CollectPage_Tooltip_FlipVertical".GetLocalized());
        ToolTipService.SetToolTip(ZoomOutButton, "CollectPage_Tooltip_ZoomOut".GetLocalized());
        ToolTipService.SetToolTip(ZoomInButton, "CollectPage_Tooltip_ZoomIn".GetLocalized());
    }

    private IEnumerable<ImageFileInfo> GetDragImagesFromItems(IList<object> dragItems)
    {
        var dragImage = dragItems.OfType<ImageFileInfo>().FirstOrDefault();
        if (dragImage == null)
            return Array.Empty<ImageFileInfo>();

        if (PreviewThumbnailGridView.SelectedItems.Contains(dragImage))
        {
            return GetOrderedSelectedImagesForDrag();
        }

        PreviewThumbnailGridView.SelectedItems.Clear();
        PreviewThumbnailGridView.SelectedItem = dragImage;
        ViewModel.SelectedImage = dragImage;
        return new[] { dragImage };
    }

    private IEnumerable<ImageFileInfo> GetOrderedSelectedImagesForDrag()
    {
        var selected = PreviewThumbnailGridView.SelectedItems
            .OfType<ImageFileInfo>()
            .ToHashSet();

        if (selected.Count == 0)
            return Array.Empty<ImageFileInfo>();

        return ViewModel.Images.Where(selected.Contains).ToList();
    }

    private void PreviewThumbnailItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _suppressNextThumbnailDragStart = IsWithinRatingControl(e.OriginalSource as DependencyObject);

        if (_suppressNextThumbnailDragStart)
            return;

        if (sender is not FrameworkElement element || element.Tag is not ImageFileInfo clickedImage)
            return;

        if (e.GetCurrentPoint(element).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        if (IsRangeSelectionModifierActive())
            return;

        var selectedImages = GetOrderedSelectedThumbnailImages();
        if (selectedImages.Count <= 1 || !selectedImages.Contains(clickedImage))
            return;

        e.Handled = true;
        UpdateSelectedImage(clickedImage, syncThumbnailSelection: false);
        RestoreThumbnailSelection(selectedImages);
        PreviewThumbnailGridView.ScrollIntoView(clickedImage, ScrollIntoViewAlignment.Default);
    }

    private static bool IsWithinRatingControl(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is RatingControl)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsRangeSelectionModifierActive()
    {
        var controlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        return controlDown || shiftDown;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectViewModel.SelectedImage))
        {
            RefreshDualPageLayout(forceRebuild: false);
            UpdateSelectedImageUi();
            if (ViewModel.SelectedImage != null)
            {
                StartSelectedThumbnailLoad(ViewModel.SelectedImage);
            }
        }
        else if (e.PropertyName == nameof(CollectViewModel.IsThumbnailStripCollapsed))
        {
            ThumbnailStripHost.Height = ViewModel.IsThumbnailStripCollapsed ? 90 : 135;
            RefreshCustomIconForegrounds();
        }
        else if (e.PropertyName == nameof(CollectViewModel.PendingDeleteCount))
        {
            UpdateShellToolbarState();
        }
        else if (e.PropertyName == nameof(CollectViewModel.SelectedSourceCount))
        {
            UpdateSourcePaneState();
        }
        else if (e.PropertyName == nameof(CollectViewModel.ThumbnailSize))
        {
            QueueVisibleThumbnailLoad("thumbnail-size-changed");
            if (ViewModel.SelectedImage != null)
            {
                StartSelectedThumbnailLoad(ViewModel.SelectedImage);
            }
        }
        else if (e.PropertyName == nameof(CollectViewModel.IsInfoDrawerOpen))
        {
            UpdateInfoDrawerState();
        }
        else if (e.PropertyName is nameof(CollectViewModel.IsDualPageMode) or nameof(CollectViewModel.DualPageMode))
        {
            RefreshDualPageLayout(forceRebuild: true);
            RefreshCustomIconForegrounds();
        }
        else if (e.PropertyName == nameof(CollectViewModel.IncludeSubfolders))
        {
            RefreshCustomIconForegrounds();
            UpdateSourcePaneState();
        }
        else if (e.PropertyName == nameof(CollectViewModel.FocusedPageSlot))
        {
            UpdatePreviewHostVisuals();
            SyncZoomControlsFromActiveCanvas();
        }
        else if (e.PropertyName is nameof(CollectViewModel.IsLoading)
            or nameof(CollectViewModel.StatusText)
            or nameof(CollectViewModel.LoadProgressValue)
            or nameof(CollectViewModel.IsLoadProgressIndeterminate))
        {
            UpdateSourcePaneState();
        }
        else if (e.PropertyName == nameof(CollectViewModel.HasLoadedPreview))
        {
            UpdateSourceLoadButtonState();
            UpdateSourcePaneState();
        }
        else if (e.PropertyName == nameof(CollectViewModel.PreviewLoadState))
        {
            UpdateSourceLoadButtonState();
        }
    }

    private void EnsurePreviewSelectionInitialized()
    {
        if (ViewModel.SelectedImage == null && ViewModel.Images.Count > 0)
        {
            UpdateSelectedImage(ViewModel.Images[0], syncThumbnailSelection: true);
        }
    }

    private void RefreshDualPageLayout(bool forceRebuild)
    {
        EnsurePreviewSelectionInitialized();

        if (!ViewModel.IsDualPageMode)
        {
            if (forceRebuild)
            {
                ViewModel.FocusedPageSlot = PreviewPageSlot.Left;
            }

            UpdateDualModeButtons();
            UpdatePreviewHostVisuals();
            SyncZoomControlsFromActiveCanvas();
            return;
        }

        if (ViewModel.DualPageMode == DualPageMode.Continuous)
        {
            RefreshContinuousPages();
        }
        else
        {
            RefreshComparePages(forceRebuild);
        }

        UpdateDualModeButtons();
        UpdatePreviewHostVisuals();
        SyncZoomControlsFromActiveCanvas();
    }

    private void RefreshComparePages(bool forceRebuild)
    {
        var selectedImage = ViewModel.SelectedImage;
        if (selectedImage == null)
        {
            ViewModel.LeftPageImage = null;
            ViewModel.RightPageImage = null;
            return;
        }

        if (forceRebuild)
        {
            ViewModel.FocusedPageSlot = PreviewPageSlot.Left;
        }

        var focusedSlot = ViewModel.FocusedPageSlot;
        if (focusedSlot == PreviewPageSlot.Left)
        {
            ViewModel.LeftPageImage = selectedImage;
            if (forceRebuild || ViewModel.RightPageImage == null)
            {
                ViewModel.RightPageImage = GetAdjacentImage(selectedImage, 1);
            }
        }
        else
        {
            ViewModel.RightPageImage = selectedImage;
            if (forceRebuild || ViewModel.LeftPageImage == null)
            {
                ViewModel.LeftPageImage = GetAdjacentImage(selectedImage, -1) ?? GetAdjacentImage(selectedImage, 1);
            }
        }
    }

    private void RefreshContinuousPages()
    {
        var selectedImage = ViewModel.SelectedImage;
        if (selectedImage == null)
        {
            ViewModel.LeftPageImage = null;
            ViewModel.RightPageImage = null;
            return;
        }

        if (ViewModel.FocusedPageSlot == PreviewPageSlot.Right)
        {
            ViewModel.RightPageImage = selectedImage;
            ViewModel.LeftPageImage = GetAdjacentImage(selectedImage, -1);
            if (ViewModel.LeftPageImage == null)
            {
                ViewModel.FocusedPageSlot = PreviewPageSlot.Left;
                ViewModel.LeftPageImage = selectedImage;
                ViewModel.RightPageImage = GetAdjacentImage(selectedImage, 1);
            }
        }
        else
        {
            ViewModel.LeftPageImage = selectedImage;
            ViewModel.RightPageImage = GetAdjacentImage(selectedImage, 1);
        }
    }

    private ImageFileInfo? GetAdjacentImage(ImageFileInfo? image, int delta)
    {
        if (image == null || ViewModel.Images.Count == 0)
            return null;

        var currentIndex = ViewModel.Images.IndexOf(image);
        if (currentIndex < 0)
            return null;

        var nextIndex = currentIndex + delta;
        if (nextIndex < 0 || nextIndex >= ViewModel.Images.Count)
            return null;

        return ViewModel.Images[nextIndex];
    }

    private void UpdateSelectedImage(ImageFileInfo? image, bool syncThumbnailSelection)
    {
        ClearSecondaryThumbnailFocus();

        if (ReferenceEquals(ViewModel.SelectedImage, image))
        {
            if (syncThumbnailSelection && image != null)
            {
                PreviewThumbnailGridView.SelectedItem = image;
                PreviewThumbnailGridView.ScrollIntoView(image, ScrollIntoViewAlignment.Default);
            }

            return;
        }

        _isUpdatingSelectedImageFromPreview = true;
        ViewModel.SelectedImage = image;
        if (syncThumbnailSelection && image != null)
        {
            PreviewThumbnailGridView.SelectedItem = image;
            PreviewThumbnailGridView.ScrollIntoView(image, ScrollIntoViewAlignment.Default);
        }
        _isUpdatingSelectedImageFromPreview = false;
    }

    private void UpdateFocusedSlot(PreviewPageSlot slot)
    {
        if (ViewModel.FocusedPageSlot == slot)
        {
            UpdatePreviewHostVisuals();
            SyncZoomControlsFromActiveCanvas();
            return;
        }

        ViewModel.FocusedPageSlot = slot;
        UpdatePreviewHostVisuals();
        SyncZoomControlsFromActiveCanvas();
    }

    private ImageFileInfo? GetFocusedPreviewImage()
    {
        return ViewModel.FocusedPageSlot == PreviewPageSlot.Left
            ? ViewModel.LeftPageImage
            : ViewModel.RightPageImage;
    }

    private PreviewImageCanvasControl GetActivePreviewCanvas()
    {
        if (!ViewModel.IsDualPageMode)
            return PreviewCanvas;

        return ViewModel.FocusedPageSlot == PreviewPageSlot.Left
            ? LeftPreviewCanvas
            : RightPreviewCanvas;
    }

    private void UpdatePreviewHostVisuals()
    {
        var accentBrush = GetThemeBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        var defaultBrush = GetThemeBrush("CardStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        if (!ViewModel.IsDualPageMode)
        {
            PreviewCanvasHost.BorderBrush = defaultBrush;
            PreviewCanvasHost.BorderThickness = new Thickness(1);
            LeftPreviewHost.BorderBrush = defaultBrush;
            LeftPreviewHost.BorderThickness = new Thickness(1);
            RightPreviewHost.BorderBrush = defaultBrush;
            RightPreviewHost.BorderThickness = new Thickness(1);
            return;
        }

        PreviewCanvasHost.BorderBrush = defaultBrush;
        PreviewCanvasHost.BorderThickness = new Thickness(1);

        var leftFocused = ViewModel.FocusedPageSlot == PreviewPageSlot.Left;
        LeftPreviewHost.BorderBrush = leftFocused ? accentBrush : defaultBrush;
        LeftPreviewHost.BorderThickness = leftFocused ? new Thickness(2) : new Thickness(1);

        var rightFocused = ViewModel.FocusedPageSlot == PreviewPageSlot.Right;
        RightPreviewHost.BorderBrush = rightFocused ? accentBrush : defaultBrush;
        RightPreviewHost.BorderThickness = rightFocused ? new Thickness(2) : new Thickness(1);
    }

    private void UpdateDualModeButtons()
    {
        if (CompareModeToggleButton == null || ContinuousModeToggleButton == null)
            return;

        CompareModeToggleButton.IsChecked = ViewModel.IsDualPageMode && ViewModel.DualPageMode == DualPageMode.Compare;
        ContinuousModeToggleButton.IsChecked = ViewModel.IsDualPageMode && ViewModel.DualPageMode == DualPageMode.Continuous;
        RightPreviewHost.IsHitTestVisible = ViewModel.DualPageMode == DualPageMode.Compare || ViewModel.RightPageImage != null;
        RefreshCustomIconForegrounds();
    }

    private void CustomIconToggleButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggleButton)
        {
            UpdateToggleContentForeground(toggleButton);
        }
    }

    private void RefreshCustomIconForegrounds()
    {
        UpdateToggleContentForeground(ThumbnailSizeToggleButton);
        UpdateToggleContentForeground(DualPageToggleButton);
        UpdateToggleContentForeground(CompareModeToggleButton);
        UpdateToggleContentForeground(ContinuousModeToggleButton);
    }

    private void UpdateSourceLoadButtonForeground()
    {
        var foregroundBrush = GetSourceLoadButtonForegroundBrush(ViewModel.PreviewLoadState);

        ApplyContentForeground(_sourceSplitButton?.Content as DependencyObject, foregroundBrush);

        if (_sourceSplitButton != null)
        {
            _sourceSplitButton.Foreground = foregroundBrush;
        }
    }

    private void UpdateToggleContentForeground(ToggleButton? button)
    {
        if (button == null)
            return;

        var foregroundBrush = GetToggleContentForegroundBrush(button);
        ApplyContentForeground(button.Content as DependencyObject, foregroundBrush);
    }

    private Brush GetToggleContentForegroundBrush(ToggleButton button)
    {
        var resourceKey = button.IsChecked == true
            ? "CollectToggleCheckedForegroundBrush"
            : "CollectToggleUncheckedForegroundBrush";

        if (Resources.TryGetValue(resourceKey, out var resource) && resource is Brush brush)
        {
            return brush;
        }

        var fallbackKey = button.IsChecked == true
            ? "TextOnAccentFillColorPrimaryBrush"
            : "TextFillColorPrimaryBrush";

        return GetThemeBrush(fallbackKey, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private Brush GetSourceLoadButtonForegroundBrush(CollectPreviewLoadState state)
    {
        const string resourceKey = "CollectToggleUncheckedForegroundBrush";

        if (Resources.TryGetValue(resourceKey, out var resource) && resource is Brush brush)
        {
            return brush;
        }

        const string fallbackKey = "TextFillColorPrimaryBrush";

        return GetThemeBrush(fallbackKey, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private static void ApplyContentForeground(DependencyObject? element, Brush foregroundBrush)
    {
        if (element == null)
            return;

        if (element is ContentControl contentControl)
        {
            contentControl.Content = foregroundBrush;
            contentControl.Foreground = foregroundBrush;
        }
        else if (element is TextBlock textBlock)
        {
            textBlock.Foreground = foregroundBrush;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                ApplyContentForeground(child, foregroundBrush);
            }
            return;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            ApplyContentForeground(VisualTreeHelper.GetChild(element, i), foregroundBrush);
        }
    }

    private void SyncZoomControlsFromActiveCanvas()
    {
        var activeCanvas = GetActivePreviewCanvas();
        UpdateZoomValueButtonContent(activeCanvas.ZoomPercent, activeCanvas.IsFitZoomActive);

        _isUpdatingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(activeCanvas.ZoomPercent, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _isUpdatingZoomSlider = false;
    }

    private void DualPageToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDualPageMode)
        {
            ViewModel.LeftPageImage = null;
            ViewModel.RightPageImage = null;
            ViewModel.FocusedPageSlot = PreviewPageSlot.Left;
        }
        else
        {
            ViewModel.DualPageMode = DualPageMode.Compare;
        }

        RefreshDualPageLayout(forceRebuild: true);
    }

    private void CompareModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDualPageMode || ViewModel.DualPageMode == DualPageMode.Compare)
        {
            UpdateDualModeButtons();
            return;
        }

        ViewModel.DualPageMode = DualPageMode.Compare;
        ViewModel.FocusedPageSlot = PreviewPageSlot.Left;
        RefreshDualPageLayout(forceRebuild: true);
    }

    private void ContinuousModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDualPageMode || ViewModel.DualPageMode == DualPageMode.Continuous)
        {
            UpdateDualModeButtons();
            return;
        }

        ViewModel.DualPageMode = DualPageMode.Continuous;
        ViewModel.FocusedPageSlot = PreviewPageSlot.Left;
        RefreshDualPageLayout(forceRebuild: true);
    }

    private void LeftPreviewHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TryActivatePreviewHost(sender, allowContinuousPromotion: true))
        {
            e.Handled = true;
        }
    }

    private void RightPreviewHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TryActivatePreviewHost(sender, allowContinuousPromotion: true))
        {
            e.Handled = true;
        }
    }

    private void PreviewHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsDualPageMode || !IsControlKeyDown())
            return;

        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        var sourceCanvas = sender is Border host && host == RightPreviewHost
            ? RightPreviewCanvas
            : LeftPreviewCanvas;
        var targetPercent = Math.Clamp(sourceCanvas.ZoomPercent + (delta > 0 ? 10d : -10d), ZoomSlider.Minimum, ZoomSlider.Maximum);

        _isUpdatingDualPageZoom = true;
        if (ViewModel.LeftPageImage != null)
        {
            LeftPreviewCanvas.SetZoomPercent(targetPercent);
        }

        if (ViewModel.RightPageImage != null)
        {
            RightPreviewCanvas.SetZoomPercent(targetPercent);
        }

        _isUpdatingDualPageZoom = false;
        SyncZoomControlsFromActiveCanvas();
        e.Handled = true;
    }

    private static bool IsControlKeyDown()
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
    }

    private bool ShouldSyncDualPreviewInteractions()
    {
        return ViewModel.IsDualPageMode && IsControlKeyDown();
    }

    private void PreviewHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        TryActivatePreviewHost(sender, allowContinuousPromotion: false);
    }

    private bool TryActivatePreviewHost(object sender, bool allowContinuousPromotion)
    {
        if (!ViewModel.IsDualPageMode)
            return false;

        if (ReferenceEquals(sender, LeftPreviewHost))
        {
            UpdateFocusedSlot(PreviewPageSlot.Left);
            if (ViewModel.LeftPageImage != null)
            {
                UpdateSelectedImage(ViewModel.LeftPageImage, syncThumbnailSelection: true);
            }

            return true;
        }

        if (!ReferenceEquals(sender, RightPreviewHost) || ViewModel.RightPageImage == null)
            return false;

        UpdateFocusedSlot(PreviewPageSlot.Right);
        if (ViewModel.RightPageImage != null)
        {
            UpdateSelectedImage(ViewModel.RightPageImage, syncThumbnailSelection: true);
        }

        return true;
    }

    private void LoadDrawerToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isLoadDrawerPinnedCollapsed = !_isLoadDrawerPinnedCollapsed;
        _isLoadDrawerTemporarilyExpanded = false;
        _ = _settingsService.SaveCollectPageLoadDrawerCollapsedAsync(_isLoadDrawerPinnedCollapsed);
        UpdateLoadDrawerState();
    }

    private void LoadDrawerRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLoadDrawerPinnedCollapsed || _isLoadDrawerTemporarilyExpanded)
            return;

        _isLoadDrawerTemporarilyExpanded = true;
        UpdateLoadDrawerState();
    }

    private void LoadDrawerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLoadDrawerPinnedCollapsed || !_isLoadDrawerTemporarilyExpanded)
            return;

        _isLoadDrawerTemporarilyExpanded = false;
        UpdateLoadDrawerState();
    }

    private bool IsLoadDrawerExpanded => !_isLoadDrawerPinnedCollapsed || _isLoadDrawerTemporarilyExpanded;

    private void CollapseLoadDrawer(bool preserveLoadProgressPanel = false)
    {
        _isLoadDrawerPinnedCollapsed = true;
        _isLoadDrawerTemporarilyExpanded = false;
        _ = _settingsService.SaveCollectPageLoadDrawerCollapsedAsync(_isLoadDrawerPinnedCollapsed);
        UpdateLoadDrawerState();
    }

    private void ExpandLoadDrawer(bool animate = true)
    {
        _isLoadDrawerPinnedCollapsed = false;
        _isLoadDrawerTemporarilyExpanded = false;
        _ = _settingsService.SaveCollectPageLoadDrawerCollapsedAsync(_isLoadDrawerPinnedCollapsed);
        UpdateLoadDrawerState(animate);
    }

    private void EnsureLoadDrawerOpenForEmptyPreview(bool animate = true)
    {
        if (ViewModel.HasLoadedPreview)
            return;

        _isLoadDrawerPinnedCollapsed = false;
        _isLoadDrawerTemporarilyExpanded = false;
        _ = _settingsService.SaveCollectPageLoadDrawerCollapsedAsync(_isLoadDrawerPinnedCollapsed);
        UpdateLoadDrawerState(animate);
    }

    private void UpdateLoadDrawerState(bool animate = true)
    {
        var isExpanded = IsLoadDrawerExpanded;
        var targetWidth = isExpanded ? LoadDrawerExpandedWidth : LoadDrawerCollapsedWidth;
        var targetChevronAngle = isExpanded ? 180 : 0;

        if (isExpanded)
        {
            SetLoadDrawerContentVisibility(Visibility.Visible);
        }

        if (!animate)
        {
            StopLoadDrawerAnimation();
            LoadDrawerRoot.Width = targetWidth;
            LoadDrawerChevronTransform.Angle = targetChevronAngle;
            if (!isExpanded)
            {
                SetLoadDrawerContentVisibility(Visibility.Collapsed);
                FinishLoadDrawerCollapsePresentation();
            }
            return;
        }

        AnimateLoadDrawer(targetWidth, targetChevronAngle, isExpanded);
    }

    private void AnimateLoadDrawer(double targetWidth, double targetChevronAngle, bool isExpanding)
    {
        StopLoadDrawerAnimation();

        var currentWidth = LoadDrawerRoot.ActualWidth > 0
            ? LoadDrawerRoot.ActualWidth
            : LoadDrawerRoot.Width;
        if (double.IsNaN(currentWidth) || currentWidth <= 0)
        {
            currentWidth = isExpanding ? LoadDrawerCollapsedWidth : LoadDrawerExpandedWidth;
        }

        if (Math.Abs(currentWidth - targetWidth) < 0.5)
        {
            LoadDrawerRoot.Width = targetWidth;
            LoadDrawerChevronTransform.Angle = targetChevronAngle;
            if (!IsLoadDrawerExpanded)
            {
                SetLoadDrawerContentVisibility(Visibility.Collapsed);
                FinishLoadDrawerCollapsePresentation();
            }
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentWidth,
            To = targetWidth,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

        Storyboard.SetTarget(animation, LoadDrawerRoot);
        Storyboard.SetTargetProperty(animation, "Width");

        var chevronAnimation = new DoubleAnimation
        {
            From = LoadDrawerChevronTransform.Angle,
            To = targetChevronAngle,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

        Storyboard.SetTarget(chevronAnimation, LoadDrawerChevronTransform);
        Storyboard.SetTargetProperty(chevronAnimation, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Children.Add(chevronAnimation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_loadDrawerStoryboard, storyboard))
                return;

            LoadDrawerRoot.Width = targetWidth;
            LoadDrawerChevronTransform.Angle = targetChevronAngle;
            if (!IsLoadDrawerExpanded)
            {
                SetLoadDrawerContentVisibility(Visibility.Collapsed);
                FinishLoadDrawerCollapsePresentation();
            }
            _loadDrawerStoryboard = null;
        };

        _loadDrawerStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopLoadDrawerAnimation()
    {
        var storyboard = _loadDrawerStoryboard;
        _loadDrawerStoryboard = null;
        storyboard?.Stop();
    }

    private void SetLoadDrawerContentVisibility(Visibility visibility)
    {
        LoadDrawerHeader.Visibility = visibility;
        LoadDrawerTree.Visibility = visibility;
    }

    private void FinishLoadDrawerCollapsePresentation()
    {
    }

    private void SyncLoadProgressPanelVisibility()
    {
    }

    private void UpdateInfoDrawerState(bool animate = true)
    {
        var isExpanded = ViewModel.IsInfoDrawerOpen;
        var targetWidth = isExpanded ? InfoDrawerExpandedWidth : InfoDrawerCollapsedWidth;

        if (isExpanded)
        {
            SetInfoDrawerContentVisibility(Visibility.Visible);
        }

        if (!animate)
        {
            StopInfoDrawerAnimation();
            PreviewInfoDrawer.Width = targetWidth;
            PreviewInfoDrawer.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        AnimateInfoDrawer(targetWidth, isExpanded);
    }

    private void AnimateInfoDrawer(double targetWidth, bool isExpanding)
    {
        StopInfoDrawerAnimation();

        var currentWidth = PreviewInfoDrawer.ActualWidth > 0
            ? PreviewInfoDrawer.ActualWidth
            : PreviewInfoDrawer.Width;
        if (double.IsNaN(currentWidth) || currentWidth < 0)
        {
            currentWidth = isExpanding ? InfoDrawerCollapsedWidth : InfoDrawerExpandedWidth;
        }

        if (Math.Abs(currentWidth - targetWidth) < 0.5)
        {
            PreviewInfoDrawer.Width = targetWidth;
            PreviewInfoDrawer.Visibility = isExpanding ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        PreviewInfoDrawer.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            From = currentWidth,
            To = targetWidth,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

        Storyboard.SetTarget(animation, PreviewInfoDrawer);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_infoDrawerStoryboard, storyboard))
                return;

            PreviewInfoDrawer.Width = targetWidth;
            if (!ViewModel.IsInfoDrawerOpen)
            {
                SetInfoDrawerContentVisibility(Visibility.Collapsed);
                PreviewInfoDrawer.Visibility = Visibility.Collapsed;
            }

            _infoDrawerStoryboard = null;
        };

        _infoDrawerStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopInfoDrawerAnimation()
    {
        var storyboard = _infoDrawerStoryboard;
        _infoDrawerStoryboard = null;
        storyboard?.Stop();
    }

    private void SetInfoDrawerContentVisibility(Visibility visibility)
    {
        PreviewInfoDrawer.Visibility = visibility;
    }

    private async void RegisterShellToolbar()
    {
        await Task.Yield();
        if (_isDisposed || _isUnloaded)
            return;

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        _sourcePane = new CollectSourcePane
        {
            SourceItems = new ObservableCollection<NavigationPaneSourceItem>(),
            ToggleOptionText = "CollectPage_IncludeAllSubfolders".GetLocalized(),
            IsToggleOptionVisible = true,
            RemoveSourceHandler = RemoveSourceFromPaneAsync,
            SourceIncludeSubfoldersHandler = SetSourceIncludeSubfoldersAsync,
            ToggleOptionHandler = value =>
            {
                ViewModel.IncludeSubfolders = value;
                UpdateSourcePaneState();
                return Task.CompletedTask;
            },
        };

        _sourceBadge = new InfoBadge
        {
            Value = 0,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-8, -8, 0, 0),
        };

        _sourceSplitButton = new SplitButton
        {
            Padding = new Thickness(12, 8, 12, 8),
            Flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
                Content = _sourcePane,
            },
        };
        _sourceSplitButton.Click += SourceSplitButton_Click;
        _sourceSplitButton.Content = CreateSourceLoadButtonContent();
        ApplyToolbarButtonChrome(_sourceSplitButton);
        toolbar.Children.Add(_sourceSplitButton);

        var exportButton = CreateToolbarButton("\uE72D", "Common_Export".GetLocalized());
        exportButton.Click += ExportButton_Click;
        toolbar.Children.Add(exportButton);

        _shellDeleteButton = CreateToolbarButton("\uE74D", "Common_Delete".GetLocalized());
        _shellDeleteButton.Click += DeleteButton_Click;
        toolbar.Children.Add(_shellDeleteButton);

        _shellFilterSplitButton = new SplitButton
        {
            Padding = new Thickness(8),
            Flyout = CreateFilterFlyout()
        };
        _shellFilterSplitButton.Content = CreateToolbarActiveIndicatorContent(
            CreateToolbarIcon("\uE71C"),
            out _shellFilterActiveIndicator);
        ApplyToolbarButtonChrome(_shellFilterSplitButton);
        _shellFilterSplitButton.Click += FilterSplitButton_Click;
        toolbar.Children.Add(_shellFilterSplitButton);

        UpdateShellToolbarState();
        UpdateFilterButtonState();
        UpdateSourceLoadButtonState();
        UpdateSourcePaneState();
        _shellToolbarService.SetToolbar(this, toolbar);
    }

    private void UpdateShellToolbarState()
    {
        if (_shellDeleteButton != null)
        {
            _shellDeleteButton.IsEnabled = ViewModel.PendingDeleteCount > 0;
        }
    }

    private Grid CreateSourceLoadButtonContent()
    {
        var foregroundBrush = GetSourceLoadButtonForegroundBrush(ViewModel.PreviewLoadState);

        var root = new Grid
        {
            IsHitTestVisible = false
        };

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });

        _sourceLoadIcon = new ContentControl
        {
            Content = foregroundBrush,
            ContentTemplate = GetSourceLoadButtonIconTemplate(ViewModel.PreviewLoadState),
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foregroundBrush
        };

        _sourceLoadText = new TextBlock
        {
            Text = GetSourceLoadButtonText(ViewModel.PreviewLoadState),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foregroundBrush
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _sourceLoadIcon,
                _sourceLoadText,
            },
        };
        Grid.SetRow(content, 0);
        root.Children.Add(content);

        _sourceLoadActiveIndicator = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 25,
            Height = 3,
            Fill = GetToolbarActiveIndicatorBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 1, 0, 0),
            RadiusX = 1.5,
            RadiusY = 1.5,
            Opacity = IsSourceLoadButtonActive(ViewModel.PreviewLoadState) ? 1 : 0
        };
        Grid.SetRow(_sourceLoadActiveIndicator, 1);
        root.Children.Add(_sourceLoadActiveIndicator);

        if (_sourceBadge != null)
        {
            Grid.SetRow(_sourceBadge, 0);
            root.Children.Add(_sourceBadge);
        }

        return root;
    }

    private async void SourceSplitButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        await LoadPreviewFromCurrentButtonStateAsync();
    }

    private void UpdateSourceLoadButtonState()
    {
        var state = ViewModel.PreviewLoadState;
        if (_sourceLoadIcon != null)
        {
            _sourceLoadIcon.ContentTemplate = GetSourceLoadButtonIconTemplate(state);
        }

        if (_sourceLoadText != null)
        {
            _sourceLoadText.Text = GetSourceLoadButtonText(state);
        }

        UpdateToolbarActiveIndicator(_sourceLoadActiveIndicator, IsSourceLoadButtonActive(state));

        if (_sourceSplitButton != null)
        {
            ApplyToolbarButtonChrome(_sourceSplitButton);
            ToolTipService.SetToolTip(_sourceSplitButton, GetSourceLoadButtonToolTip(state));
        }

        UpdateSourceLoadButtonForeground();
    }

    private void UpdateSourcePaneState()
    {
        if (_sourcePane == null)
        {
            _shellToolbarService.UpdateProgress(
                ViewModel.IsLoading,
                ViewModel.IsLoadProgressIndeterminate,
                ViewModel.LoadProgressValue);
            return;
        }

        _sourcePane.Subtitle = $"{ViewModel.SelectedSourceCount} / {ViewModel.MaxSourceCount}";
        _sourcePane.StatusText = ViewModel.StatusText;
        _sourcePane.ToggleOptionValue = ViewModel.IncludeSubfolders;

        _sourcePane.SourceItems.Clear();
        foreach (var source in ViewModel.SelectedSources)
        {
            _sourcePane.SourceItems.Add(new NavigationPaneSourceItem
            {
                Id = source.Path,
                DisplayName = source.DisplayName,
                IncludeSubfolders = source.IncludeSubfolders,
                Payload = source
            });
        }

        _sourcePane.HasSourceItems = _sourcePane.SourceItems.Count > 0;

        if (_sourceBadge != null)
        {
            _sourceBadge.Value = ViewModel.SelectedSourceCount;
            _sourceBadge.Visibility = ViewModel.SelectedSourceCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        _shellToolbarService.UpdateProgress(
            ViewModel.IsLoading,
            ViewModel.IsLoadProgressIndeterminate,
            ViewModel.LoadProgressValue);
    }

    private async Task SetSourceIncludeSubfoldersAsync(NavigationPaneSourceItem item, bool includeSubfolders)
    {
        if (item.Payload is PreviewSource source &&
            source.IncludeSubfolders != includeSubfolders)
        {
            source.IncludeSubfolders = includeSubfolders;
            ViewModel.RefreshPreviewLoadState();
            UpdateSourcePaneState();
        }

        await Task.CompletedTask;
    }

    private async Task RemoveSourceFromPaneAsync(NavigationPaneSourceItem item)
    {
        if (item.Payload is PreviewSource source)
        {
            ViewModel.RemoveSource(source);
            UpdateSourcePaneState();
        }

        await Task.CompletedTask;
    }

    private async Task LoadPreviewFromPaneAsync(bool forceRefresh = false)
    {
        var result = await ViewModel.LoadPreviewAsync(forceRefresh);
        if (result.AutoCollapseDrawer)
        {
            CollapseLoadDrawer();
        }
        else if (!result.LoadedAnyFiles)
        {
            ExpandLoadDrawer();
        }

        UpdateSourcePaneState();
        QueueVisibleThumbnailLoad("load-preview");
    }

    private Task LoadPreviewFromCurrentButtonStateAsync()
    {
        return LoadPreviewFromPaneAsync(ViewModel.PreviewLoadState == CollectPreviewLoadState.Refresh);
    }

    private static DataTemplate? GetSourceLoadButtonIconTemplate(CollectPreviewLoadState state)
    {
        var resourceKey = state switch
        {
            CollectPreviewLoadState.Append => "CollectAppendIconTemplate",
            CollectPreviewLoadState.Refresh => "CollectRefreshLoadIconTemplate",
            _ => "CollectLoadIconTemplate",
        };

        return Application.Current.Resources.TryGetValue(resourceKey, out var resource) && resource is DataTemplate template
            ? template
            : null;
    }

    private static string GetSourceLoadButtonText(CollectPreviewLoadState state)
    {
        return state switch
        {
            CollectPreviewLoadState.Append => "CollectPage_Append".GetLocalized(),
            CollectPreviewLoadState.Refresh => "CollectPage_Refresh".GetLocalized(),
            _ => "CollectPage_Load.Text".GetLocalized(),
        };
    }

    private static string GetSourceLoadButtonToolTip(CollectPreviewLoadState state)
    {
        return state switch
        {
            CollectPreviewLoadState.Append => "CollectPage_AppendLoad".GetLocalized(),
            CollectPreviewLoadState.Refresh => "CollectPage_RefreshLoad".GetLocalized(),
            _ => "CollectPage_Load.Text".GetLocalized(),
        };
    }

    private static bool IsSourceLoadButtonActive(CollectPreviewLoadState state)
    {
        return state is CollectPreviewLoadState.Append or CollectPreviewLoadState.Refresh;
    }

    private static Button CreateToolbarButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Padding = new Thickness(8),
            Content = CreateToolbarIcon(glyph)
        };
        ApplyToolbarButtonChrome(button);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static void ApplyToolbarButtonChrome(Control control)
    {
        var transparentBrush = GetThemeBrush("TransparentFillColor", Microsoft.UI.Colors.Transparent);
        var disabledForegroundBrush = GetThemeBrush("TextFillColorDisabledBrush", Windows.UI.Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));

        control.MinWidth = 40;
        control.Height = 40;
        control.Background = transparentBrush;
        control.BorderBrush = transparentBrush;
        control.BorderThickness = new Thickness(0);
        control.Resources["ButtonBackgroundDisabled"] = transparentBrush;
        control.Resources["ButtonBorderBrushDisabled"] = transparentBrush;
        control.Resources["ButtonForegroundDisabled"] = disabledForegroundBrush;
    }

    private static FontIcon CreateToolbarIcon(string glyph)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = 16
        };
    }

    private static Brush GetToolbarActiveIndicatorBrush()
    {
        return GetThemeBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    }

    private static Brush GetThemeBrush(string resourceKey, Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallbackColor);
    }

    private static Grid CreateToolbarActiveIndicatorContent(FrameworkElement icon, out Microsoft.UI.Xaml.Shapes.Rectangle indicator)
    {
        var root = new Grid
        {
            Width = 24,
            Height = 24,
            IsHitTestVisible = false
        };

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });

        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetRow(icon, 0);
        root.Children.Add(icon);

        indicator = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 25,
            Height = 3,
            Fill = GetToolbarActiveIndicatorBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 1, 0, 0),
            RadiusX = 1.5,
            RadiusY = 1.5,
            Opacity = 0
        };

        Grid.SetRow(indicator, 1);
        root.Children.Add(indicator);

        return root;
    }

    private static void UpdateToolbarActiveIndicator(Microsoft.UI.Xaml.Shapes.Rectangle? indicator, bool isActive)
    {
        if (indicator != null)
        {
            indicator.Opacity = isActive ? 1 : 0;
        }
    }

    private Flyout CreateFilterFlyout()
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };
        flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            Setters =
            {
                new Setter(FrameworkElement.WidthProperty, 700d),
                new Setter(FrameworkElement.MinWidthProperty, 700d),
                new Setter(FrameworkElement.MaxWidthProperty, 700d),
                new Setter(Control.PaddingProperty, new Thickness(16)),
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left),
                new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Top)
            }
        };
        flyout.Opening += (_, _) =>
        {
            if (flyout.Content == null)
            {
                flyout.Content = new FilterFlyout
                {
                    FilterViewModel = ViewModel.Filter,
                    ShowBurstFilter = true
                };
            }
        };
        return flyout;
    }

    private void Filter_FilterChanged(object? sender, EventArgs e)
    {
        UpdateFilterButtonState();
    }

    private void UpdateFilterButtonState()
    {
        var isActive = ViewModel.Filter.IsFilterActive;
        UpdateToolbarActiveIndicator(_shellFilterActiveIndicator, isActive);

        if (_shellFilterSplitButton != null)
        {
            ApplyToolbarButtonChrome(_shellFilterSplitButton);
        }
    }

    private int _selectedImageVersion;

    private async void UpdateSelectedImageUi()
    {
        var imageInfo = ViewModel.SelectedImage;
        if (imageInfo == null)
        {
            PreviewInfoViewModel.Clear();
            return;
        }

        var version = ++_selectedImageVersion;
        PreviewInfoViewModel.SetBasicInfo(imageInfo);

        await PreviewInfoViewModel.LoadFileDetailsAsync(imageInfo.ImageFile, imageInfo.DateTaken);

        if (version != _selectedImageVersion)
        {
            return;
        }
    }

    private void StartSelectedThumbnailLoad(ImageFileInfo imageInfo)
    {
        if (_isDisposed || _isUnloaded)
            return;

        var version = ++_selectedThumbnailLoadVersion;
        _thumbnailCoordinator.PendingVisibleThumbnailLoads.Remove(imageInfo);
        DebugThumbnailLoad($"Selected thumbnail start image={GetDebugName(imageInfo)} version={version}");
        _ = LoadSelectedThumbnailAsync(imageInfo, version);
    }

    private async Task LoadSelectedThumbnailAsync(ImageFileInfo imageInfo, int version)
    {
        try
        {
            await imageInfo.EnsureFastPreviewAsync(ViewModel.ThumbnailSize);
            if (!IsCurrentSelectedThumbnailLoad(imageInfo, version))
            {
                DebugThumbnailLoad($"Selected thumbnail skip stale after fast image={GetDebugName(imageInfo)} version={version}");
                return;
            }

            await imageInfo.EnsureThumbnailAsync(ViewModel.ThumbnailSize);
            if (!IsCurrentSelectedThumbnailLoad(imageInfo, version))
            {
                DebugThumbnailLoad($"Selected thumbnail completed stale image={GetDebugName(imageInfo)} version={version}");
                return;
            }

            DebugThumbnailLoad($"Selected thumbnail complete image={GetDebugName(imageInfo)} version={version}");
        }
        catch (OperationCanceledException)
        {
            DebugThumbnailLoad($"Selected thumbnail canceled image={GetDebugName(imageInfo)} version={version}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectPage] selected thumbnail load failed: {ex.Message}");
        }
    }

    private bool IsCurrentSelectedThumbnailLoad(ImageFileInfo imageInfo, int version)
    {
        return !_isDisposed &&
            !_isUnloaded &&
            version == _selectedThumbnailLoadVersion &&
            ReferenceEquals(ViewModel.SelectedImage, imageInfo);
    }

    private void PreviewInfoViewModel_RatingUpdated(object? sender, (ImageFileInfo Image, uint Rating) e)
    {
        e.Image.Rating = e.Rating;
        QueueVisibleThumbnailLoad("preview-info-rating-updated");
    }

    private void PreviewThumbnailRatingControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RatingControl ratingControl && !_ratingControlEventMap.ContainsKey(ratingControl))
        {
            ratingControl.ValueChanged += PreviewThumbnailRatingControl_ValueChanged;
            _ratingControlEventMap[ratingControl] = true;
        }
    }

    private void PreviewThumbnailRatingControl_ValueChanged(RatingControl sender, object args)
    {
        try
        {
            if (_isDisposed || _isUnloaded)
                return;

            var imageInfo = FindImageInfoFromRatingControl(sender);
            if (imageInfo == null)
                return;

            var controlValue = sender.Value;
            uint ratingValue = 0;
            if (controlValue > 0)
            {
                var stars = (int)Math.Round(controlValue, MidpointRounding.AwayFromZero);
                ratingValue = ImageFileInfo.StarsToRating(Math.Clamp(stars, 1, 5));
            }

            _pendingRatingUpdate = (imageInfo, ratingValue);
            _ratingDebounceTimer.Stop();
            _ratingDebounceTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectPage] rating change failed: {ex.Message}");
        }
    }

    private async void RatingDebounceTimer_Tick(object? sender, object e)
    {
        _ratingDebounceTimer.Stop();
        if (!_pendingRatingUpdate.HasValue)
            return;

        var (image, rating) = _pendingRatingUpdate.Value;
        _pendingRatingUpdate = null;

        var imagesToProcess = GetImagesForRatingUpdate(image);
        foreach (var imageInfo in imagesToProcess)
        {
            await UpdateRatingAsync(imageInfo, rating);
        }
    }

    private static ImageFileInfo? FindImageInfoFromRatingControl(RatingControl ratingControl)
    {
        try
        {
            var parent = VisualTreeHelper.GetParent(ratingControl);
            while (parent != null)
            {
                if (parent is FrameworkElement { DataContext: ImageFileInfo imageInfo })
                {
                    return imageInfo;
                }

                parent = VisualTreeHelper.GetParent(parent);
            }
        }
        catch
        {
        }

        return null;
    }

    private List<ImageFileInfo> GetImagesForRatingUpdate(ImageFileInfo image)
    {
        var imagesToProcess = new List<ImageFileInfo>();
        if (_settingsService.AutoSyncGroupRatings && image.Group != null)
        {
            foreach (var groupImage in image.Group.Images)
            {
                if (!imagesToProcess.Contains(groupImage))
                {
                    imagesToProcess.Add(groupImage);
                }
            }
        }
        else
        {
            imagesToProcess.Add(image);
        }

        return imagesToProcess;
    }

    private static async Task UpdateRatingAsync(ImageFileInfo imageInfo, uint rating)
    {
        try
        {
            var ratingService = App.GetService<RatingService>();
            await imageInfo.SetRatingAsync(ratingService, rating);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CollectPage] update rating failed: {ex.Message}");
        }
    }

    private void PreviewCanvas_ZoomPercentChanged(object? sender, double percent)
    {
        if (sender is not PreviewImageCanvasControl canvas)
            return;

        if (!_isUpdatingDualPageZoom && !ReferenceEquals(canvas, GetActivePreviewCanvas()))
            return;

        _isUpdatingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);
        UpdateZoomValueButtonContent(percent, canvas.IsFitZoomActive);
        _isUpdatingZoomSlider = false;
    }

    private void PreviewCanvas_ViewportStateChanged(object? sender, PreviewViewportState state)
    {
        if (sender is not PreviewImageCanvasControl sourceCanvas ||
            _isSyncingPreviewViewportState ||
            !ShouldSyncDualPreviewInteractions())
        {
            return;
        }

        var targetCanvas = ReferenceEquals(sourceCanvas, LeftPreviewCanvas)
            ? RightPreviewCanvas
            : LeftPreviewCanvas;

        var targetImage = ReferenceEquals(targetCanvas, LeftPreviewCanvas)
            ? ViewModel.LeftPageImage
            : ViewModel.RightPageImage;
        if (targetImage == null)
            return;

        _isSyncingPreviewViewportState = true;
        targetCanvas.ApplyViewportState(state);
        _isSyncingPreviewViewportState = false;
    }

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingZoomSlider)
            return;

        if (ShouldSyncDualPreviewInteractions())
        {
            if (ViewModel.LeftPageImage != null)
            {
                LeftPreviewCanvas.SetZoomPercent(e.NewValue);
            }

            if (ViewModel.RightPageImage != null)
            {
                RightPreviewCanvas.SetZoomPercent(e.NewValue);
            }
        }
        else
        {
            var activeCanvas = GetActivePreviewCanvas();
            activeCanvas.SetZoomPercent(e.NewValue);
            UpdateZoomValueButtonContent(e.NewValue, activeCanvas.IsFitZoomActive);
            return;
        }

        SyncZoomControlsFromActiveCanvas();
    }

    private void ZoomValueButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShouldSyncDualPreviewInteractions())
        {
            if (ViewModel.LeftPageImage != null)
            {
                LeftPreviewCanvas.ToggleOriginalOrFitZoom();
            }

            if (ViewModel.RightPageImage != null)
            {
                RightPreviewCanvas.ToggleOriginalOrFitZoom();
            }
        }
        else
        {
            var activeCanvas = GetActivePreviewCanvas();
            activeCanvas.ToggleOriginalOrFitZoom();
        }

        SyncZoomControlsFromActiveCanvas();
    }
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        StepZoomPercent(-10d);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        StepZoomPercent(10d);
    }

    private void StepZoomPercent(double deltaPercent)
    {
        var activeCanvas = GetActivePreviewCanvas();
        var targetPercent = Math.Clamp(activeCanvas.ZoomPercent + deltaPercent, ZoomSlider.Minimum, ZoomSlider.Maximum);

        if (ShouldSyncDualPreviewInteractions())
        {
            if (ViewModel.LeftPageImage != null)
            {
                LeftPreviewCanvas.SetZoomPercent(targetPercent);
            }

            if (ViewModel.RightPageImage != null)
            {
                RightPreviewCanvas.SetZoomPercent(targetPercent);
            }
        }
        else
        {
            activeCanvas.SetZoomPercent(targetPercent);
        }

        ZoomSlider.Value = targetPercent;
        UpdateZoomValueButtonContent(targetPercent, activeCanvas.IsFitZoomActive);
    }

    private void UpdateZoomValueButtonContent(double percent, bool isFitZoomActive)
    {
        var iconLayer = new Grid
        {
            Width = 20,
            Height = 20
        };

        iconLayer.Children.Add(new FontIcon
        {
            Glyph = "\uE9A6",
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        if (isFitZoomActive)
        {
            iconLayer.Children.Add(new FontIcon
            {
                Glyph = "\uE915",
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            });
        }

        ZoomValueButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                iconLayer,
                new TextBlock
                {
                    Text = $"{percent:F0}%",
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private void RotatePreview_Click(object sender, RoutedEventArgs e)
    {
        if (ShouldSyncDualPreviewInteractions())
        {
            if (ViewModel.LeftPageImage != null)
            {
                LeftPreviewCanvas.RotateClockwise();
            }

            if (ViewModel.RightPageImage != null)
            {
                RightPreviewCanvas.RotateClockwise();
            }

            return;
        }

        GetActivePreviewCanvas().RotateClockwise();
    }

    private void FlipPreviewHorizontal_Click(object sender, RoutedEventArgs e)
    {
        if (ShouldSyncDualPreviewInteractions())
        {
            if (ViewModel.LeftPageImage != null)
            {
                LeftPreviewCanvas.FlipHorizontal();
            }

            if (ViewModel.RightPageImage != null)
            {
                RightPreviewCanvas.FlipHorizontal();
            }

            return;
        }

        GetActivePreviewCanvas().FlipHorizontal();
    }

    private void FlipPreviewVertical_Click(object sender, RoutedEventArgs e)
    {
        if (ShouldSyncDualPreviewInteractions())
        {
            if (ViewModel.LeftPageImage != null)
            {
                LeftPreviewCanvas.FlipVertical();
            }

            if (ViewModel.RightPageImage != null)
            {
                RightPreviewCanvas.FlipVertical();
            }

            return;
        }

        GetActivePreviewCanvas().FlipVertical();
    }

    private void ToggleInfoDrawer_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsInfoDrawerOpen = !ViewModel.IsInfoDrawerOpen;
    }

    private void FilterSplitButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (ViewModel.Filter.IsFilterActive)
        {
            ViewModel.Filter.Reset();
        }
        else
        {
            if (sender.Flyout is FlyoutBase flyout)
            {
                flyout.ShowAt(sender, new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
                });
            }
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ExportDialog(
            _settingsService,
            ViewModel.GetFilteredExportImages(),
            ViewModel.GetLoadedExportImages(),
            ViewModel.IsFilterActive)
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var pendingImages = ViewModel.GetPendingDeleteImages();
        if (pendingImages.Count == 0)
            return;

        var allExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in pendingImages)
        {
            allExtensions.Add(Path.GetExtension(image.ImageFile.Path).ToLowerInvariant());
            if (image.AlternateFormats != null)
            {
                foreach (var alternate in image.AlternateFormats)
                {
                    allExtensions.Add(Path.GetExtension(alternate.ImageFile.Path).ToLowerInvariant());
                }
            }
        }

        var dialog = new DeleteConfirmDialog(allExtensions.Where(ext => !string.IsNullOrWhiteSpace(ext)).ToList(), pendingImages.Count)
        {
            XamlRoot = XamlRoot
        };

        dialog.ConfirmDeleteAsync = async currentDialog =>
        {
            var filesToDelete = GetFilesToDelete(pendingImages, currentDialog.SelectedExtensions);
            if (filesToDelete.Count == 0)
            {
                currentDialog.SetComplete();
                return;
            }

            var imagePathMap = DeleteWorkflowHelper.BuildPrimaryImagePathMap(pendingImages);
            var deleteResult = await DeleteWorkflowHelper.DeleteFilesAsync(
                filesToDelete,
                imagePathMap,
                currentDialog,
                App.GetService<IThumbnailService>(),
                message => System.Diagnostics.Debug.WriteLine($"[CollectPage] delete failed {message}"));

            ViewModel.RemoveDeletedImages(deleteResult.DeletedImages);
            QueueVisibleThumbnailLoad("delete");
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        var filesToDelete = GetFilesToDelete(pendingImages, dialog.SelectedExtensions);
        if (filesToDelete.Count == 0)
            return;

        var imagePathMap = DeleteWorkflowHelper.BuildPrimaryImagePathMap(pendingImages);
        var deleteResult = await DeleteWorkflowHelper.DeleteFilesAsync(
            filesToDelete,
            imagePathMap,
            dialog,
            App.GetService<IThumbnailService>(),
            message => System.Diagnostics.Debug.WriteLine($"[CollectPage] delete failed {message}"));

        ViewModel.RemoveDeletedImages(deleteResult.DeletedImages);
        QueueVisibleThumbnailLoad("delete");
        return;
    }

    private static List<StorageFile> GetFilesToDelete(List<ImageFileInfo> pendingImages, List<string> selectedExtensions)
    {
        return DeleteWorkflowHelper.GetFilesToDelete(pendingImages, selectedExtensions);
    }

    private static async Task DeleteFileToRecycleBinAsync(StorageFile file)
    {
        var settingsService = App.GetService<ISettingsService>();
        var driveRoot = Path.GetPathRoot(file.Path);
        var removable = !string.IsNullOrWhiteSpace(driveRoot) &&
            DriveInfo.GetDrives().Any(drive =>
                string.Equals(drive.Name, driveRoot, StringComparison.OrdinalIgnoreCase) &&
                drive.DriveType == DriveType.Removable);

        var deleteOption = removable || !settingsService.DeleteToRecycleBin
            ? StorageDeleteOption.PermanentDelete
            : StorageDeleteOption.Default;
        await file.DeleteAsync(deleteOption);
    }

    private bool HandleShortcut(KeyRoutedEventArgs e)
    {
        if (_isDisposed || _isUnloaded)
            return false;

        if (e.Key == VirtualKey.Space)
        {
            var selectedImages = PreviewThumbnailGridView.SelectedItems
                .OfType<ImageFileInfo>()
                .ToList();

            if (selectedImages.Count > 1)
            {
                var retainedImage = ViewModel.SelectedImage
                    ?? PreviewThumbnailGridView.SelectedItem as ImageFileInfo
                    ?? selectedImages[0];

                PreviewThumbnailGridView.SelectedItems.Clear();
                PreviewThumbnailGridView.SelectedItem = retainedImage;
                UpdateSelectedImage(retainedImage, syncThumbnailSelection: false);
                PreviewThumbnailGridView.ScrollIntoView(retainedImage, ScrollIntoViewAlignment.Default);
                return true;
            }

            return false;
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.SelectedImage != null)
        {
            if (ShouldSyncDualPreviewInteractions())
            {
                if (ViewModel.LeftPageImage != null)
                {
                    LeftPreviewCanvas.ResetToFitZoom();
                }

                if (ViewModel.RightPageImage != null)
                {
                    RightPreviewCanvas.ResetToFitZoom();
                }
            }
            else
            {
                GetActivePreviewCanvas().ResetToFitZoom();
            }

            SyncZoomControlsFromActiveCanvas();
            return true;
        }
        else if (e.Key == VirtualKey.Tab && ViewModel.IsDualPageMode)
        {
            var nextSlot = GetNextPreviewSlot();
            UpdateFocusedSlot(nextSlot);
            var focusedImage = GetFocusedPreviewImage();
            if (focusedImage != null)
            {
                UpdateSelectedImage(focusedImage, syncThumbnailSelection: true);
            }

            (nextSlot == PreviewPageSlot.Left ? LeftPreviewHost : RightPreviewHost).Focus(FocusState.Programmatic);
            return true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            ViewModel.TogglePendingDeleteForSelected(PreviewThumbnailGridView.SelectedItems.OfType<ImageFileInfo>());
            return true;
        }
        else if (e.Key >= VirtualKey.Number0 && e.Key <= VirtualKey.Number5)
        {
            HandleRatingShortcut(e.Key);
            return true;
        }
        else if (e.Key >= VirtualKey.NumberPad0 && e.Key <= VirtualKey.NumberPad5)
        {
            HandleRatingShortcut(e.Key - (VirtualKey.NumberPad0 - VirtualKey.Number0));
            return true;
        }
        else if (TryGetDirectionalNavigationDelta(e.Key, out var direction))
        {
            if (ShouldProcessDirectionalNavigation(e.KeyStatus.WasKeyDown, direction))
            {
                MoveSelection(direction);
            }

            return true;
        }

        return false;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        var current = element;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void HandleRatingShortcut(VirtualKey key)
    {
        var selectedImages = PreviewThumbnailGridView.SelectedItems
            .OfType<ImageFileInfo>()
            .ToList();

        if (selectedImages.Count == 0)
        {
            if (ViewModel.SelectedImage == null)
                return;

            selectedImages.Add(ViewModel.SelectedImage);
        }

        var stars = key - VirtualKey.Number0;
        var imagesToProcess = new List<ImageFileInfo>();
        foreach (var selectedImage in selectedImages)
        {
            foreach (var imageInfo in GetImagesForRatingUpdate(selectedImage))
            {
                if (!imagesToProcess.Contains(imageInfo))
                {
                    imagesToProcess.Add(imageInfo);
                }
            }
        }

        foreach (var imageInfo in imagesToProcess)
        {
            uint newRating;
            if (stars == 0)
            {
                newRating = 0;
            }
            else
            {
                var currentStars = ImageFileInfo.RatingToStars(imageInfo.Rating);
                newRating = currentStars == stars
                    ? 0
                    : ImageFileInfo.StarsToRating(stars);
            }

            _ = UpdateRatingAsync(imageInfo, newRating);
        }
    }

    private void MoveSelection(int delta)
    {
        if (ViewModel.Images.Count == 0)
            return;

        ClearSecondaryThumbnailFocus();

        if (TryMoveWithinSelectedImages(delta))
            return;

        if (!ViewModel.IsDualPageMode)
        {
            var currentIndex = ViewModel.SelectedImage != null
                ? ViewModel.Images.IndexOf(ViewModel.SelectedImage)
                : -1;
            var nextIndex = Math.Clamp(currentIndex + delta, 0, ViewModel.Images.Count - 1);
            var nextImage = ViewModel.Images[nextIndex];
            UpdateSelectedImage(nextImage, syncThumbnailSelection: true);
            return;
        }

        if (ViewModel.DualPageMode == DualPageMode.Continuous)
        {
            var currentImage = ViewModel.SelectedImage ?? ViewModel.Images[0];
            var nextImage = GetClampedAdjacentImage(currentImage, delta);
            if (nextImage != null)
            {
                UpdateSelectedImage(nextImage, syncThumbnailSelection: true);
            }

            return;
        }

        var focusedImage = GetFocusedPreviewImage() ?? ViewModel.SelectedImage ?? ViewModel.Images[0];
        var compareNextImage = GetClampedAdjacentImage(focusedImage, delta);
        if (compareNextImage == null)
            return;

        if (ViewModel.FocusedPageSlot == PreviewPageSlot.Left)
        {
            ViewModel.LeftPageImage = compareNextImage;
        }
        else
        {
            ViewModel.RightPageImage = compareNextImage;
        }

        UpdateSelectedImage(compareNextImage, syncThumbnailSelection: true);
        UpdatePreviewHostVisuals();
        SyncZoomControlsFromActiveCanvas();
    }

    private bool TryMoveWithinSelectedImages(int delta)
    {
        var selectedImages = GetOrderedSelectedThumbnailImages();
        if (selectedImages.Count <= 1)
            return false;

        var currentImage = GetFocusedPreviewImage() ?? ViewModel.SelectedImage;
        var currentIndex = currentImage != null
            ? selectedImages.IndexOf(currentImage)
            : -1;
        if (currentIndex < 0)
        {
            currentIndex = delta > 0 ? -1 : selectedImages.Count;
        }

        var nextIndex = Math.Clamp(currentIndex + delta, 0, selectedImages.Count - 1);
        var nextImage = selectedImages[nextIndex];
        ApplyPreviewSelectionWithinMultiSelect(nextImage, selectedImages);
        return true;
    }

    private List<ImageFileInfo> GetOrderedSelectedThumbnailImages()
    {
        var selectedSet = PreviewThumbnailGridView.SelectedItems
            .OfType<ImageFileInfo>()
            .ToHashSet();

        if (selectedSet.Count == 0)
            return new List<ImageFileInfo>();

        return ViewModel.Images
            .Where(selectedSet.Contains)
            .ToList();
    }

    private void ApplyPreviewSelectionWithinMultiSelect(ImageFileInfo image, IReadOnlyList<ImageFileInfo> selectedImages)
    {
        if (ViewModel.IsDualPageMode && ViewModel.DualPageMode == DualPageMode.Compare)
        {
            if (ViewModel.FocusedPageSlot == PreviewPageSlot.Left)
            {
                ViewModel.LeftPageImage = image;
            }
            else
            {
                ViewModel.RightPageImage = image;
            }
        }

        _isUpdatingSelectedImageFromPreview = true;
        ViewModel.SelectedImage = image;
        SetSecondaryThumbnailFocus(image);
        RestoreThumbnailSelection(selectedImages);
        PreviewThumbnailGridView.ScrollIntoView(image, ScrollIntoViewAlignment.Default);
        _isUpdatingSelectedImageFromPreview = false;
        RefreshDualPageLayout(forceRebuild: false);
        UpdateSelectedImageUi();
        UpdatePreviewHostVisuals();
        SyncZoomControlsFromActiveCanvas();
    }

    private void RestoreThumbnailSelection(IReadOnlyList<ImageFileInfo> selectedImages)
    {
        var selectedSet = selectedImages.ToHashSet();
        var currentSelection = PreviewThumbnailGridView.SelectedItems
            .OfType<ImageFileInfo>()
            .ToList();

        foreach (var image in currentSelection)
        {
            if (!selectedSet.Contains(image))
            {
                PreviewThumbnailGridView.SelectedItems.Remove(image);
            }
        }

        foreach (var image in selectedImages)
        {
            if (!PreviewThumbnailGridView.SelectedItems.Contains(image))
            {
                PreviewThumbnailGridView.SelectedItems.Add(image);
            }
        }
    }

    private void SetSecondaryThumbnailFocus(ImageFileInfo? image)
    {
        if (ReferenceEquals(_secondaryFocusedThumbnail, image))
        {
            if (image != null && !image.IsSecondaryFocused)
            {
                image.IsSecondaryFocused = true;
            }

            return;
        }

        if (_secondaryFocusedThumbnail != null)
        {
            _secondaryFocusedThumbnail.IsSecondaryFocused = false;
        }

        _secondaryFocusedThumbnail = image;

        if (_secondaryFocusedThumbnail != null)
        {
            _secondaryFocusedThumbnail.IsSecondaryFocused = true;
        }
    }

    private void ClearSecondaryThumbnailFocus()
    {
        if (_secondaryFocusedThumbnail == null)
            return;

        _secondaryFocusedThumbnail.IsSecondaryFocused = false;
        _secondaryFocusedThumbnail = null;
    }

    private ImageFileInfo? GetClampedAdjacentImage(ImageFileInfo image, int delta)
    {
        var currentIndex = ViewModel.Images.IndexOf(image);
        if (currentIndex < 0)
            return null;

        var nextIndex = Math.Clamp(currentIndex + delta, 0, ViewModel.Images.Count - 1);
        return ViewModel.Images[nextIndex];
    }

    private PreviewPageSlot GetNextPreviewSlot()
    {
        if (ViewModel.FocusedPageSlot == PreviewPageSlot.Left && ViewModel.RightPageImage != null)
        {
            return PreviewPageSlot.Right;
        }

        return PreviewPageSlot.Left;
    }

    private static bool TryGetDirectionalNavigationDelta(VirtualKey key, out int direction)
    {
        if (key == VirtualKey.Left || key == VirtualKey.Up)
        {
            direction = -1;
            return true;
        }

        if (key == VirtualKey.Right || key == VirtualKey.Down)
        {
            direction = 1;
            return true;
        }

        direction = 0;
        return false;
    }

    private bool ShouldProcessDirectionalNavigation(bool wasKeyDown, int direction)
    {
        var now = Environment.TickCount64;
        if (!wasKeyDown ||
            _lastDirectionalNavigationDirection != direction ||
            now - _lastDirectionalNavigationTick >= DirectionalNavigationRepeatIntervalMs)
        {
            _lastDirectionalNavigationDirection = direction;
            _lastDirectionalNavigationTick = now;
            return true;
        }

        return false;
    }

    private static string GetDebugName(ImageFileInfo? imageInfo)
    {
        if (imageInfo == null)
            return "<null>";

        return string.IsNullOrWhiteSpace(imageInfo.ImageFile?.Name)
            ? imageInfo.ImageName
            : imageInfo.ImageFile.Name;
    }

    [Conditional("DEBUG")]
    private static void DebugThumbnailLoad(string message)
    {
        //Debug.WriteLine($"[CollectThumbnailLoad] {DateTime.Now:HH:mm:ss.fff} {message}");
    }
}



