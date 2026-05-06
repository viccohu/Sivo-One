using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PhotoView.Contracts.Services;
using PhotoView.Controls;
using PhotoView.Dialogs;
using PhotoView.Helpers;
using PhotoView.Models;
using PhotoView.Services;
using PhotoView.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace PhotoView.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    private readonly DispatcherTimer _loadImagesThrottleTimer;
    private readonly DispatcherTimer _ratingDebounceTimer;
    private readonly ISettingsService _settingsService;
    private readonly ShellToolbarService _shellToolbarService;
    private readonly IThumbnailService _thumbnailService;
    private readonly MainPageThumbnailCoordinator _thumbnailCoordinator;
    private readonly HashSet<ImageFileInfo> _selectedImageState = new();
    private readonly Dictionary<ImageFileInfo, int> _imageIndexMap = new();
    private const double GridViewItemMargin = 4d;
    private const double GridViewItemGap = GridViewItemMargin * 2d;
    private const double NavigationDrawerExpandedWidth = 265d;
    private const double NavigationDrawerCollapsedWidth = 25d;
    private const double FolderDrawerExpandedMaxHeight = 260d;
    private const double FolderDrawerCollapsedOffsetY = -8d;
    private const double ImageGridTopScrollTolerance = 1d;
    private const int FolderDrawerAnimationDurationMs = 220;
    private const int FastPreviewStartBudgetPerTick = 24;
    private const int TargetThumbnailStartBudgetPerTick = 8;
    private const int FastPreviewPrefetchScreenCount = 8;
    private const int TargetThumbnailPrefetchItemCount = 4;
    private const int TargetThumbnailRetainScreenCount = 2;
    private const int MaxTargetThumbnailRetainedItems = 160;
    private const int WarmPreviewIdleBudgetPerTick = 3;
    private const int WarmPreviewScrollBudgetPerTick = 1;
    private const int MaxActiveWarmPreviewLoads = 2;
    private const int DirectionalNavigationRepeatIntervalMs = 180;
    private const int DirectionalNavigationScopeGrid = 1;
    private const int DirectionalNavigationScopeViewer = 2;
    private const uint WarmPreviewLongSidePixels = 160;
    private readonly PointerEventHandler _imageGridPointerWheelHandler;
    private readonly KeyEventHandler _imageGridKeyDownHandler;
    private readonly IKeyboardShortcutService _shortcutService;
    private FolderNode? _pendingLoadNode;
    private ScrollViewer? _imageGridScrollViewer;
    private ItemsWrapGrid? _imageItemsWrapGrid;
    private bool _isUnloaded;
    private bool _isProgrammaticScrollActive;
    private bool _isUserScrollInProgress;
    private bool _hasAttemptedRestoreLastFolder;
    private bool _isFolderDrawerExpanded = true;
    private bool _isFolderDrawerContentVisible;
    private bool _isNavigationDrawerPinnedCollapsed;
    private bool _isNavigationDrawerTemporarilyExpanded;
    private bool _isImageGridPointerWheelHandlerAttached;
    private bool _isImageGridKeyDownHandlerAttached;
    private bool _isHandlingDirectionalBurstNavigation;
    private bool _isSwitchingViewerImage;
    private bool _suppressNextImageDragStart;
    private double _lastImageGridVerticalOffset;
    private int _folderDrawerAnimationVersion;
    private int _lastDirectionalNavigationScope;
    private int _lastDirectionalNavigationDirection;
    private long _lastDirectionalNavigationTick;
    private Storyboard? _navigationDrawerStoryboard;
    private Storyboard? _folderDrawerStoryboard;
    private (ImageFileInfo Image, uint Rating)? _pendingRatingUpdate;
    private ImageFileInfo? _storedImageFileInfo;
    private Controls.ImageViewerControl? _currentViewer;
    private Button? _shellDeleteButton;
    private ToggleButton? _shellAutoExpandBurstToggleButton;
    private SplitButton? _shellFilterSplitButton;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _shellAutoExpandBurstActiveIndicator;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _shellFilterActiveIndicator;

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        _settingsService = App.GetService<ISettingsService>();
        _shellToolbarService = App.GetService<ShellToolbarService>();
        _thumbnailService = App.GetService<IThumbnailService>();
        _shortcutService = App.GetService<IKeyboardShortcutService>();
        
        NavigationCacheMode = NavigationCacheMode.Enabled;
        InitializeComponent();
        ApplyLocalizedToolTips();
        _imageGridPointerWheelHandler = ImageGridView_PointerWheelChanged;
        _imageGridKeyDownHandler = ImageGridView_KeyDown;
        FolderTreeView.DataContext = ViewModel;

        _loadImagesThrottleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _loadImagesThrottleTimer.Tick += LoadImagesThrottleTimer_Tick;

        _ratingDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _ratingDebounceTimer.Tick += RatingDebounceTimer_Tick;

        _thumbnailCoordinator = new MainPageThumbnailCoordinator(TimeSpan.FromMilliseconds(50));
        _thumbnailCoordinator.VisibleThumbnailLoadTimer.Tick += VisibleThumbnailLoadTimer_Tick;

        ViewModel.ImagesChanged += ViewModel_ImagesChanged;
        ViewModel.ThumbnailSizeChanged += ViewModel_ThumbnailSizeChanged;
        ViewModel.FolderTreeLoaded += ViewModel_FolderTreeLoaded;
        ViewModel.SelectedFolderChanged += ViewModel_SelectedFolderChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        
        _shortcutService.RegisterPageShortcutHandler("MainPage", HandleShortcut);
        
        if (ViewModel.FolderTree.Count > 0)
        {
            _ = TryRestoreLastFolderAsync();
        }
    }

    private void ApplyLocalizedToolTips()
    {
        ToolTipService.SetToolTip(BackButton, "Common_Back".GetLocalized());
        ToolTipService.SetToolTip(UpButton, "MainPage_Tooltip_GoUp".GetLocalized());
        ToolTipService.SetToolTip(RefreshButton, "Common_Refresh".GetLocalized());
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        FilterFlyoutControl.FilterViewModel = ViewModel.Filter;
        RegisterShellToolbar();
        ViewModel.Filter.FilterChanged += Filter_FilterChanged;
        ImageGridView.DoubleTapped += ImageGridView_DoubleTapped;
        ImageGridView.Tapped += ImageGridView_Tapped;
        AttachImageGridKeyDown();
        AttachImageGridScrollViewer();
        AttachImageGridPointerWheel();
        UpdateImageGridTileSize();
        RebuildImageIndexMap();
        QueueVisibleThumbnailLoad("page-loaded");
        UpdateFilterButtonState();
        UpdateNavigationDrawerState(animate: false);
        UpdateFolderDrawerState(animate: false);
        ReattachActiveViewerAfterNavigation();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _shortcutService.SetCurrentPage("MainPage");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _shortcutService.SetCurrentPage("");
    }

    private async void ImageViewer_Closed(object? sender, EventArgs e)
    {
        var viewer = _currentViewer;
        if (viewer == null)
        {
            return;
        }

        try
        {
            if (_storedImageFileInfo != null)
            {
                await ScrollItemIntoViewAsync(_storedImageFileInfo, "viewer-close", ScrollIntoViewAlignment.Default);

                var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("BackConnectedAnimation");
                if (animation != null)
                {
                    await ImageGridView.TryStartConnectedAnimationAsync(animation, _storedImageFileInfo, "thumbnailImage");
                }
            }

            await viewer.CompleteCloseAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] ImageViewer_Closed error: {ex}");
        }
        finally
        {
            var imageToFocus = _storedImageFileInfo;
            // 婵炴挸鎳愰幃濠勬導閸曨剛鐖?
            viewer.Closed -= ImageViewer_Closed;
            viewer.ViewModel.RatingUpdated -= ViewModel_RatingUpdated;
            if (ReferenceEquals(_currentViewer, viewer))
            {
                ViewerContainer.Content = null;
                _currentViewer = null;
            }

            if (imageToFocus != null)
            {
                await SelectImageForViewerAsync(imageToFocus, "viewer-close-focus", focusThumbnail: true);
            }

            await _settingsService.ResumeAlwaysDecodeRawPersistenceAsync("viewer-close");
        }
    }

    private void ReattachActiveViewerAfterNavigation()
    {
        if (ViewerContainer.Content is not Controls.ImageViewerControl viewer)
            return;

        _currentViewer = viewer;
        viewer.Closed -= ImageViewer_Closed;
        viewer.Closed += ImageViewer_Closed;
        viewer.ViewModel.RatingUpdated -= ViewModel_RatingUpdated;
        viewer.ViewModel.RatingUpdated += ViewModel_RatingUpdated;
        viewer.ReactivateAfterNavigation();
        _settingsService.SuspendAlwaysDecodeRawPersistence("viewer-return");
    }

    private void ViewModel_RatingUpdated(object? sender, (ImageFileInfo Image, uint Rating) e)
    {
        // System.Diagnostics.Debug.WriteLine($"[MainPage] ViewModel_RatingUpdated: 闁搞儱澧芥晶?{e.Image.ImageName} 閻犲洤瀚鍥ь啅閸欏绾柡鍌烆暒鐠?{e.Rating}");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SubFolderCount) ||
            e.PropertyName == nameof(MainViewModel.HasSubFoldersInCurrentFolder) ||
            e.PropertyName == nameof(MainViewModel.CurrentSubFolders))
        {
            SetFolderDrawerExpanded(ViewModel.HasSubFoldersInCurrentFolder, "subfolders-changed");
        }
        else if (e.PropertyName == nameof(MainViewModel.PendingDeleteCount))
        {
            UpdateShellToolbarState();
        }
        else if (e.PropertyName is nameof(MainViewModel.IsLoadingImages)
            or nameof(MainViewModel.IsImageLoadProgressIndeterminate)
            or nameof(MainViewModel.ImageLoadProgressValue))
        {
            UpdateGlobalLoadProgress();
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

    private MenuFlyout CreateThumbnailSizeFlyout()
    {
        var flyout = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };

        foreach (var size in new[] { "Small", "Medium", "Large" })
        {
            var item = new MenuFlyoutItem
            {
                Text = $"MainPage_Size{size}.Text".GetLocalized(),
                Tag = size
            };
            item.Click += ThumbnailSize_Click;
            flyout.Items.Add(item);
        }

        return flyout;
    }

    private async void ImageGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is ImageFileInfo imageFileInfo)
        {
            await OpenImageViewerAsync(imageFileInfo, useConnectedAnimation: true);
            e.Handled = true;
        }
    }

    private async Task OpenImageViewerAsync(ImageFileInfo imageFileInfo, bool useConnectedAnimation)
    {
        if (_isUnloaded || AppLifetime.IsShuttingDown)
            return;

        if (_currentViewer != null)
            return;

        AutoCollapseImageBrowsingChrome("viewer-open");
        await SelectImageForViewerAsync(imageFileInfo, "viewer-open", focusThumbnail: false);
        _storedImageFileInfo = imageFileInfo;

        var newViewer = new Controls.ImageViewerControl();
        ViewerContainer.Content = newViewer;
        _currentViewer = newViewer;
        _settingsService.SuspendAlwaysDecodeRawPersistence("viewer-open");

        newViewer.Closed += ImageViewer_Closed;
        newViewer.ViewModel.RatingUpdated += ViewModel_RatingUpdated;

        newViewer.PrepareContent(imageFileInfo);
        await newViewer.PrepareForAnimationAsync();

        if (useConnectedAnimation && ImageGridView.ContainerFromItem(imageFileInfo) is GridViewItem)
        {
            ImageGridView.PrepareConnectedAnimation("ForwardConnectedAnimation", imageFileInfo, "thumbnailImage");
        }

        var imageAnimation = ConnectedAnimationService.GetForCurrentView().GetAnimation("ForwardConnectedAnimation");
        if (imageAnimation != null)
        {
            imageAnimation.TryStart(newViewer.GetMainImage(), newViewer.GetCoordinatedElements());
        }

        await newViewer.ShowAfterAnimationAsync();
    }

    private async Task ToggleImageViewerForCurrentSelectionAsync()
    {
        if (_currentViewer != null)
        {
            _currentViewer.PrepareCloseAnimation();
            return;
        }

        var currentImage = GetCurrentImageForViewerOrSelection();
        if (currentImage != null)
        {
            await OpenImageViewerAsync(currentImage, useConnectedAnimation: true);
        }
    }

    private async Task SwitchViewerImageAsync(int delta)
    {
        if (_isSwitchingViewerImage)
            return;

        var viewer = _currentViewer;
        if (viewer == null)
            return;

        var currentImage = GetCurrentImageForViewerOrSelection();
        if (currentImage == null)
            return;

        _isSwitchingViewerImage = true;
        try
        {
            BurstPhotoGroup? burstGroupToCollapse = null;
            var nextImage = GetDirectionalNavigationImage(currentImage, delta, out burstGroupToCollapse);
            if (nextImage == null || ReferenceEquals(nextImage, currentImage))
            {
                if (nextImage == null)
                    return;

                _storedImageFileInfo = nextImage;
                await SelectImageForViewerAsync(nextImage, "viewer-key-burst-expand", focusThumbnail: false);
                return;
            }

            _storedImageFileInfo = nextImage;
            await SelectImageForViewerAsync(nextImage, "viewer-key-switch", focusThumbnail: false);
            await viewer.SwitchImageAsync(nextImage);

            if (burstGroupToCollapse != null)
            {
                ViewModel.CollapseBurstGroup(burstGroupToCollapse);
                await SelectImageForViewerAsync(nextImage, "viewer-key-collapse-burst", focusThumbnail: false);
            }
        }
        finally
        {
            _isSwitchingViewerImage = false;
        }
    }

    private ImageFileInfo? GetDirectionalNavigationImage(
        ImageFileInfo currentImage,
        int delta,
        out BurstPhotoGroup? burstGroupToCollapse)
    {
        burstGroupToCollapse = null;

        if (!_settingsService.CollapseBurstGroups || !_settingsService.AutoExpandBurstOnDirectionalNavigation)
        {
            return GetAdjacentImage(currentImage, delta);
        }

        if (currentImage.BurstGroup is { Images.Count: > 1, IsExpanded: false })
        {
            var expandedTarget = ViewModel.ExpandBurstForDirectionalNavigation(currentImage, delta);
            return expandedTarget ?? currentImage;
        }

        var currentExpandedGroup = currentImage.BurstGroup?.IsExpanded == true
            ? currentImage.BurstGroup
            : null;
        var nextImage = GetAdjacentImage(currentImage, delta);
        if (nextImage == null)
            return null;

        if (nextImage.BurstGroup is { Images.Count: > 1, IsExpanded: false })
        {
            nextImage = ViewModel.ExpandBurstForDirectionalNavigation(nextImage, delta) ?? nextImage;
        }

        if (currentExpandedGroup != null && !ReferenceEquals(nextImage.BurstGroup, currentExpandedGroup))
        {
            burstGroupToCollapse = currentExpandedGroup;
        }

        return nextImage;
    }

    private async Task SelectImageForViewerAsync(ImageFileInfo imageInfo, string reason, bool focusThumbnail)
    {
        if (!ViewModel.Images.Contains(imageInfo))
            return;

        ExecuteProgrammaticSelectionChange(() =>
        {
            ImageGridView.SelectedItems.Clear();
            ImageGridView.SelectedItem = imageInfo;
        });

        await ScrollItemIntoViewAsync(imageInfo, reason, ScrollIntoViewAlignment.Default);

        if (focusThumbnail && ImageGridView.ContainerFromItem(imageInfo) is GridViewItem container)
        {
            container.Focus(FocusState.Programmatic);
        }
    }

    private ImageFileInfo? GetCurrentImageForViewerOrSelection()
    {
        if (_currentViewer != null && _storedImageFileInfo != null && ViewModel.Images.Contains(_storedImageFileInfo))
            return _storedImageFileInfo;

        if (ImageGridView.SelectedItem is ImageFileInfo selectedImage && ViewModel.Images.Contains(selectedImage))
            return selectedImage;

        var firstSelectedImage = ImageGridView.SelectedItems
            .OfType<ImageFileInfo>()
            .FirstOrDefault(ViewModel.Images.Contains);
        if (firstSelectedImage != null)
            return firstSelectedImage;

        return ViewModel.Images.FirstOrDefault();
    }

    private ImageFileInfo? GetAdjacentImage(ImageFileInfo currentImage, int delta)
    {
        if (ViewModel.Images.Count == 0)
            return null;

        if (!TryGetImageIndex(currentImage, out var currentIndex))
            currentIndex = ViewModel.Images.IndexOf(currentImage);

        if (currentIndex < 0)
            return null;

        var nextIndex = Math.Clamp(currentIndex + delta, 0, ViewModel.Images.Count - 1);
        return ViewModel.Images[nextIndex];
    }

    private void Filter_FilterChanged(object? sender, EventArgs e)
    {
        UpdateFilterButtonState();
    }

    private void UpdateFilterButtonState()
    {
        var isActive = ViewModel.Filter.IsFilterActive;
        UpdateToolbarActiveIndicator(_shellFilterActiveIndicator, isActive);
        
        if (isActive)
        {
            if (_shellFilterSplitButton != null)
            {
                ApplyToolbarButtonChrome(_shellFilterSplitButton);
            }
        }
        else
        {
            FilterSplitButton.ClearValue(Control.BackgroundProperty);
            if (_shellFilterSplitButton != null)
            {
                ApplyToolbarButtonChrome(_shellFilterSplitButton);
            }
        }
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

    private async void ViewModel_FolderTreeLoaded(object? sender, EventArgs e)
    {
        // System.Diagnostics.Debug.WriteLine($"[MainPage] FolderTreeLoaded 濞存粌顑勫▎銏㈡喆閿曗偓瑜?);
        await TryRestoreLastFolderAsync();
    }

    private async void ViewModel_SelectedFolderChanged(object? sender, FolderNode? node)
    {
        if (_isUnloaded || AppLifetime.IsShuttingDown || node == null)
            return;

        // System.Diagnostics.Debug.WriteLine($"[MainPage] SelectedFolderChanged 濞存粌顑勫▎銏㈡喆閿曗偓瑜? 闁煎搫鍊婚崑? {node.Name}");
        await ExpandTreeViewPathAsync(node);
    }

    private async System.Threading.Tasks.Task TryRestoreLastFolderAsync()
    {
        if (_hasAttemptedRestoreLastFolder)
            return;

        _hasAttemptedRestoreLastFolder = true;

        var settingsService = App.GetService<ISettingsService>();
        
        // 缂佹稑顦欢鐔烘媼閸撗呮瀭闁告梻濮惧ù鍥┾偓鐟版湰閸ㄦ岸鏁嶉崼鐔镐粯濠㈣埖姘ㄩ悺鎴濐嚗?2 缂佸甯槐?
        int waitCount = 0;
        while (waitCount < 20)
        {
            // 濠碘€冲€归悘?RememberLastFolder 濞?true 濞戞挻妫侀惌鎯ь嚗閸曨亞鐟濆☉鎾规閳规牠鏁嶇仦钘夎濞寸姰鍎辩槐鎴炴叏鐎ｎ偂鍒掑?
            if (settingsService.RememberLastFolder && !string.IsNullOrEmpty(settingsService.LastFolderPath))
                break;
            
            // 濠碘€冲€归悘?RememberLastFolder 濞?false 濞戞挻鏌ㄩ崙锛勭磼韫囨洜鎼肩€垫澘鎳撶换鍐╃▔閳ь剚娼鍡欑閻犲洤鐡ㄥΣ鎴犳媼閸撗呮瀭鐎瑰憡褰冩慨鐐存姜閹存帞鐟柡鍫簻閹酣鎮?
            if (!settingsService.RememberLastFolder && waitCount > 0)
            {
                // System.Diagnostics.Debug.WriteLine($"[MainPage] 闁哄牜浜滈幆搴ㄦ偨閵婎煈鍞跺ù锝呯箣缁楀倸鈻庨檱閻儳顕ラ崟鍓佺閻犲搫鐤囩换鍐箒閵忕媭妲?);
                return;
            }
            
            await System.Threading.Tasks.Task.Delay(100);
            waitCount++;
        }
        
        // System.Diagnostics.Debug.WriteLine($"[MainPage] FolderTreeLoaded 閻熸瑱绠戣ぐ? RememberLastFolder={settingsService.RememberLastFolder}, LastFolderPath={settingsService.LastFolderPath}, 缂佹稑顦欢鐔封枎閳╁啯娈?{waitCount}");
        
        if (!settingsService.RememberLastFolder || string.IsNullOrEmpty(settingsService.LastFolderPath))
        {
            // System.Diagnostics.Debug.WriteLine($"[MainPage] 閻犱警鍨扮欢鐐寸▔閾忓厜鏁勯柟瀛樼墬濠€顓㈠触椤栨粍鏆忛柨娑樼焷閻戯附娼婚崶銊ゅ垝濠?);
            return;
        }

        await System.Threading.Tasks.Task.Delay(200);
        if (_isUnloaded || AppLifetime.IsShuttingDown)
            return;

        // System.Diagnostics.Debug.WriteLine($"[MainPage] 閻忓繑绻嗛惁顖炲箒閵忕媭妲诲☉鎾筹攻椤愯偐鎹勯姘辩獮: {settingsService.LastFolderPath}");

        var targetNode = await TryFindAndLoadNodeByPathAsync(settingsService.LastFolderPath);
        if (targetNode != null)
        {
            await ExpandTreeViewPathAsync(targetNode);
            if (_isUnloaded || AppLifetime.IsShuttingDown)
                return;
            ThrottleLoadImages(targetNode);
        }
        else
        {
            // System.Diagnostics.Debug.WriteLine($"[MainPage] 闁哄牜浜濇竟姗€宕氭０浣虹憪婵炲枴銈囩唴鐎? {settingsService.LastFolderPath}");
        }
    }

    private async System.Threading.Tasks.Task<FolderNode?> TryFindAndLoadNodeByPathAsync(string targetPath)
    {
        targetPath = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // System.Diagnostics.Debug.WriteLine($"[TryFindAndLoadNodeByPathAsync] 闁烩晩鍠楅悥锝囨崉椤栨氨绐? {targetPath}");

        var foundNode = ViewModel.FindNodeByPath(targetPath);
        if (foundNode != null)
            return foundNode;

        var pathParts = targetPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length == 0)
            return null;

        var currentPath = pathParts[0] + Path.DirectorySeparatorChar;
        FolderNode? currentNode = null;

        foreach (var rootNode in ViewModel.FolderTree)
        {
            if (rootNode.NodeType == NodeType.ThisPC || rootNode.NodeType == NodeType.ExternalDevice)
            {
                if (!rootNode.IsLoaded)
                {
                    await ViewModel.LoadChildrenAsync(rootNode);
                }

                foreach (var child in rootNode.Children)
                {
                    if (child.FullPath != null &&
                        string.Equals(
                            child.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentNode = child;
                        break;
                    }
                }

                if (currentNode == null)
                {
                    foreach (var child in rootNode.Children)
                    {
                        if (child.FullPath != null && child.FullPath.StartsWith(currentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            currentNode = child;
                            break;
                        }
                    }
                }

                if (currentNode != null)
                    break;
            }
        }

        if (currentNode == null)
            return null;

        for (int i = 1; i < pathParts.Length; i++)
        {
            if (!currentNode.IsLoaded)
            {
                await ViewModel.LoadChildrenAsync(currentNode);
            }

            var nextPart = pathParts[i];
            currentPath = Path.Combine(currentPath, nextPart);

            FolderNode? nextNode = null;
            foreach (var child in currentNode.Children)
            {
                if (child.FullPath != null && string.Equals(child.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                {
                    nextNode = child;
                    break;
                }
            }

            if (nextNode == null)
                return null;

            currentNode = nextNode;
        }

        // System.Diagnostics.Debug.WriteLine($"[TryFindAndLoadNodeByPathAsync] 闁哄牃鍋撶紓浣哥墛婢规﹢宕氶幏灞轿濋柣? {currentNode.Name}, 閻庣懓鏈弳锝囨崉椤栨氨绐? {currentNode.FullPath}");
        return currentNode;
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _shellToolbarService.UpdateProgress(false, false, 0d);
        _shellToolbarService.ClearToolbar(this);
        _shellDeleteButton = null;
        _shellAutoExpandBurstToggleButton = null;
        _shellFilterSplitButton = null;
        _shellAutoExpandBurstActiveIndicator = null;
        _shellFilterActiveIndicator = null;
        if (_currentViewer != null)
        {
            _currentViewer.Closed -= ImageViewer_Closed;
            _currentViewer.ViewModel.RatingUpdated -= ViewModel_RatingUpdated;
            _currentViewer = null;
            _ = _settingsService.ResumeAlwaysDecodeRawPersistenceAsync("main-page-unloaded");
        }

        ViewModel.Filter.FilterChanged -= Filter_FilterChanged;
        ImageGridView.DoubleTapped -= ImageGridView_DoubleTapped;
        ImageGridView.Tapped -= ImageGridView_Tapped;
        DetachImageGridKeyDown();
        _loadImagesThrottleTimer.Stop();
        _ratingDebounceTimer.Stop();
        _thumbnailCoordinator.ResetState();
        StopNavigationDrawerAnimation();
        _pendingRatingUpdate = null;
        _isSwitchingViewerImage = false;
        _lastDirectionalNavigationScope = 0;
        _lastDirectionalNavigationDirection = 0;
        _lastDirectionalNavigationTick = 0;
        _selectedImageState.Clear();
        DetachImageGridPointerWheel();
        DetachImageGridScrollViewer();
        _imageItemsWrapGrid = null;
        
        foreach (var ratingControl in _ratingControlEventMap.Keys)
        {
            ratingControl.ValueChanged -= RatingControl_ValueChanged;
        }
        _ratingControlEventMap.Clear();
    }

    private async void LoadImagesThrottleTimer_Tick(object? sender, object e)
    {
        _loadImagesThrottleTimer.Stop();

        if (_pendingLoadNode == null || _isUnloaded || AppLifetime.IsShuttingDown)
        {
            return;
        }

        var node = _pendingLoadNode;
        _pendingLoadNode = null;

        try
        {
            await ViewModel.LoadImagesAsync(node);
        }
        catch (System.Threading.Tasks.TaskCanceledException)
        {
        }
    }

    private void ViewModel_ThumbnailSizeChanged(object? sender, EventArgs e)
    {
        if (_isUnloaded || AppLifetime.IsShuttingDown)
        {
            return;
        }

        ClearThumbnailQueues();
        UpdateImageGridTileSize();
        TriggerVisibleItemsThumbnailLoad();
    }

    private void ImageGridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImageGridTileSize();
    }

    private void UpdateImageGridTileSize()
    {
        if (_isUnloaded || AppLifetime.IsShuttingDown)
            return;

        AttachImageItemsWrapGrid();
        if (_imageItemsWrapGrid == null)
            return;

        var availableWidth = ImageGridView.ActualWidth - ImageGridView.Padding.Left - ImageGridView.Padding.Right;
        if (availableWidth <= 0)
            return;

        var targetTileSize = GetTargetTileSize(ViewModel.ThumbnailSize);
        var minimumTileSize = GetMinimumTileSize(ViewModel.ThumbnailSize);
        var maximumTileSize = GetMaximumTileSize(ViewModel.ThumbnailSize);

        var columnCount = Math.Max(1, (int)Math.Floor((availableWidth + GridViewItemGap) / (targetTileSize + GridViewItemGap)));
        var tileSize = CalculateTileSize(availableWidth, columnCount);

        while (columnCount > 1 && tileSize < minimumTileSize)
        {
            columnCount--;
            tileSize = CalculateTileSize(availableWidth, columnCount);
        }

        while (tileSize > maximumTileSize)
        {
            columnCount++;
            tileSize = CalculateTileSize(availableWidth, columnCount);
        }

        tileSize = Math.Clamp(tileSize, minimumTileSize, maximumTileSize);

        if (Math.Abs(_imageItemsWrapGrid.ItemWidth - tileSize) < 0.5 &&
            Math.Abs(_imageItemsWrapGrid.ItemHeight - tileSize) < 0.5)
        {
            return;
        }

        _imageItemsWrapGrid.ItemWidth = tileSize;
        _imageItemsWrapGrid.ItemHeight = tileSize;
        // System.Diagnostics.Debug.WriteLine($"[MainPage] Tile size updated: size={tileSize:F0}, columns={columnCount}, width={availableWidth:F0}");

        QueueVisibleThumbnailLoad("tile-size-changed");
    }

    private static double CalculateTileSize(double availableWidth, int columnCount)
    {
        return Math.Max(1d, (availableWidth - (GridViewItemGap * Math.Max(0, columnCount - 1))) / columnCount);
    }

    private static double GetTargetTileSize(ThumbnailSize size)
    {
        return size switch
        {
            ThumbnailSize.Small => 160d,
            ThumbnailSize.Medium => 256d,
            ThumbnailSize.Large => 512d,
            _ => 256d
        };
    }

    private static double GetMinimumTileSize(ThumbnailSize size)
    {
        return size switch
        {
            ThumbnailSize.Small => 140d,
            ThumbnailSize.Medium => 208d,
            ThumbnailSize.Large => 360d,
            _ => 208d
        };
    }

    private static double GetMaximumTileSize(ThumbnailSize size)
    {
        return size switch
        {
            ThumbnailSize.Small => 200d,
            ThumbnailSize.Medium => 320d,
            ThumbnailSize.Large => 640d,
            _ => 320d
        };
    }

    private void TriggerVisibleItemsThumbnailLoad()
    {
        try
        {
            // 閻熸瑱绠戣ぐ鍌滅箔椤戣法顏辩€殿喚濮村ù姗€鎮ч崶鈺傜暠闁告梻濮惧ù?
            if (ViewModel.Images.Count > 0)
            {
                var firstImage = ViewModel.Images[0];
                QueueVisibleThumbnailLoad(ViewModel.Images[0], "thumbnail-size-first");
            }

            // 閻熸瑱绠戣ぐ鍌炲箥閳ь剟寮垫径濠傚笚缂佽京濮峰▓鎴﹀礉閻樼儤绁伴柨娑樼灱閳ユɑ绌卞┑鍡樻閻庣數顭堥崹蹇涘箲閵忊剝顦ч柟纰樺亾闁哄牆顦ù姗€鎮ч崶顒€鍘撮柤鍏呯矙閸ｆ悂寮弶鍨潱閺?
            QueueVisibleThumbnailLoad("thumbnail-size-changed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TriggerVisibleItemsThumbnailLoad error: {ex}");
        }
    }

    private void ThumbnailSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string sizeString)
        {
            if (Enum.TryParse<ThumbnailSize>(sizeString, out var size))
            {
                ViewModel.ThumbnailSize = size;
            }
        }
    }

    private async void FolderTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FolderNode node)
        {
            var navigationNode = ViewModel.IsNodeUnderFavoritesRoot(node) && !string.IsNullOrEmpty(node.FullPath)
                ? (ViewModel.FindNodeByPath(node.FullPath!) ?? node)
                : node;

            FolderTreeView.SelectedItem = navigationNode;
            await ViewModel.LoadChildrenAsync(navigationNode);
            if (_isUnloaded || AppLifetime.IsShuttingDown)
            {
                return;
            }

            ThrottleLoadImages(navigationNode);
        }
    }

    private void FolderTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderNode node)
        {
            var navigationNode = ViewModel.IsNodeUnderFavoritesRoot(node) && !string.IsNullOrEmpty(node.FullPath)
                ? (ViewModel.FindNodeByPath(node.FullPath!) ?? node)
                : node;

            if (sender.ContainerFromItem(navigationNode) is TreeViewItem treeViewItem
                && navigationNode.HasExpandableChildren
                && !treeViewItem.IsExpanded)
            {
                treeViewItem.IsExpanded = true;
            }

            ThrottleLoadImages(navigationNode);
        }
    }

    private void ThrottleLoadImages(FolderNode node)
    {
        if (IsCurrentDisplayedNode(node) || _pendingLoadNode == node)
        {
            return;
        }

        _pendingLoadNode = node;
        _loadImagesThrottleTimer.Stop();
        _loadImagesThrottleTimer.Start();
    }

    private bool IsCurrentDisplayedNode(FolderNode node)
    {
        var selectedFolder = ViewModel.SelectedFolder;
        if (selectedFolder == null)
        {
            return false;
        }

        if (ReferenceEquals(selectedFolder, node))
        {
            return true;
        }

        return selectedFolder.NodeType == node.NodeType
            && !string.IsNullOrEmpty(selectedFolder.FullPath)
            && string.Equals(selectedFolder.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    private async void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is FolderNode node)
        {
            await NavigateToFolderNodeAsync(node);
        }
    }

    private async Task NavigateToFolderNodeAsync(FolderNode node)
    {
        await ExpandTreeViewPathAsync(node);
        if (_isUnloaded || AppLifetime.IsShuttingDown)
        {
            return;
        }

        ThrottleLoadImages(node);
    }


    private async void SubFolderGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TryGetFolderNodeFromElement(e.OriginalSource, out var folderNode) && folderNode.Folder != null)
        {
            SubFolderGridView.SelectedItem = folderNode;
            await NavigateToFolderNodeAsync(folderNode);
            e.Handled = true;
        }
    }

    private static bool TryGetFolderNodeFromElement(object originalSource, out FolderNode folderNode)
    {
        var current = originalSource as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement element)
            {
                if (element.Tag is FolderNode tagNode)
                {
                    folderNode = tagNode;
                    return true;
                }

                if (element.DataContext is FolderNode contextNode)
                {
                    folderNode = contextNode;
                    return true;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        folderNode = null!;
        return false;
    }

    private async System.Threading.Tasks.Task ExpandTreeViewPathAsync(FolderNode targetNode)
    {
        try
        {
            if (_isUnloaded || AppLifetime.IsShuttingDown)
            {
                return;
            }

            // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 鐎殿喒鍋撳┑顔碱儏閻秴顕ｉ埀? 闁烩晩鍠楅悥锝夋嚍閸屾粌浠? {targetNode.Name}, 閻犱警鍨扮欢? {targetNode.FullPath}");

            var path = new List<FolderNode>();
            var current = targetNode;
            while (current != null)
            {
                path.Insert(0, current);
                // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 閻犱警鍨扮欢鐐烘嚍閸屾粌浠? {current.Name}, NodeType={current.NodeType}, Parent={current.Parent?.Name ?? "null"}");
                current = current.Parent;
            }

            // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 閻犱警鍨扮欢鐐烘⒐閸喖顔? {path.Count}");

            if (path.Count == 0)
            {
                return;
            }

            FolderNode? lastNode = null;
            for (var i = 0; i < path.Count; i++)
            {
                var node = path[i];
                lastNode = node;
                // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 濠㈣泛瀚幃濠囨嚍閸屾粌浠?{i}: {node.Name}, IsLoaded={node.IsLoaded}, IsExpanded={node.IsExpanded}, Children.Count={node.Children.Count}");

                if (i < path.Count - 1)
                {
                    if (!node.IsLoaded)
                    {
                        // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 闁告梻濮惧ù鍥┾偓娑欏姌婵☆參鎮? {node.Name}");
                        await ViewModel.LoadChildrenAsync(node);
                        if (_isUnloaded || AppLifetime.IsShuttingDown)
                        {
                            return;
                        }
                    }

                    if (!node.IsExpanded)
                    {
                        // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 閻忕偞娲栫槐鎴︽嚍閸屾粌浠? {node.Name}");
                        node.IsExpanded = true;
                        await System.Threading.Tasks.Task.Delay(50);
                    }
                }

                var treeViewItem = FolderTreeView.ContainerFromItem(node) as TreeViewItem;
                // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] TreeViewItem for {node.Name}: {(treeViewItem != null ? "found" : "null")}");
            }

            if (lastNode != null)
            {
                // System.Diagnostics.Debug.WriteLine($"[ExpandTreeViewPathAsync] 閻犱礁澧介悿鍡涙焻婢跺鍘柤鍝勫€婚崑? {lastNode.Name}");
                FolderTreeView.SelectedItem = lastNode;
                await System.Threading.Tasks.Task.Delay(100);
                if (_isUnloaded || AppLifetime.IsShuttingDown)
                {
                    return;
                }

                ScrollToTreeViewItem(lastNode);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExpandTreeViewPathAsync error: {ex}");
        }
    }

    private void ScrollToTreeViewItem(FolderNode node)
    {
        try
        {
            if (FolderTreeView.ContainerFromItem(node) is TreeViewItem container)
            {
                container.StartBringIntoView();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScrollToTreeViewItem error: {ex}");
        }
    }

    private void ViewModel_ImagesChanged(object? sender, EventArgs e)
    {
        if (_isUnloaded || AppLifetime.IsShuttingDown)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            RebuildImageIndexMap();

            if (ViewModel.Images.Count == 0)
            {
                // System.Diagnostics.Debug.WriteLine("[MainPage] ImagesChanged -> clearing transient grid state for empty collection");
                ClearGridViewSelection();
                _thumbnailCoordinator.ResetState();
                ClearSelectedImageState();
            }
            else
            {
                QueueBackgroundPreviewWarmup(0, ViewModel.Images.Count - 1, prioritize: false);
            }

            QueueVisibleThumbnailLoad("images-changed");
        });
    }

    private void ImageGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ImageFileInfo imageInfo)
            return;

        if (args.InRecycleQueue)
        {
            imageInfo.CancelTargetThumbnailLoad();
            _thumbnailCoordinator.MarkItemRecycled(imageInfo);
            return;
        }

        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1u, ImageGridView_ContainerContentChanging);
        }
        else if (args.Phase == 1)
        {
            _thumbnailCoordinator.MarkItemRealized(imageInfo);
            TryStartImmediateFastPreviewLoad(imageInfo, "container-phase1");
            QueueVisibleThumbnailLoad("container-phase1");
        }
    }


    private readonly Dictionary<RatingControl, bool> _ratingControlEventMap = new();

    private void RatingControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RatingControl ratingControl && !_ratingControlEventMap.ContainsKey(ratingControl))
        {
            ratingControl.ValueChanged += RatingControl_ValueChanged;
            _ratingControlEventMap[ratingControl] = true;
        }
    }

    private void RatingControl_ValueChanged(RatingControl sender, object args)
    {
        try
        {
            if (_isUnloaded || AppLifetime.IsShuttingDown)
                return;

            var imageInfo = FindImageInfoFromRatingControl(sender);
            if (imageInfo == null)
                return;

            if (!imageInfo.CanEditGridRating)
                return;

            if (imageInfo.IsRatingLoading && !imageInfo.IsRatingLoaded)
                return;

            double controlValue = sender.Value;
            uint ratingValue = 0;

            if (controlValue > 0)
            {
                int stars = (int)Math.Round(controlValue, MidpointRounding.AwayFromZero);
                stars = Math.Clamp(stars, 1, 5);
                ratingValue = ImageFileInfo.StarsToRating(stars);
            }

            _pendingRatingUpdate = (imageInfo, ratingValue);
            _ratingDebounceTimer.Stop();
            _ratingDebounceTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RatingControl_ValueChanged] 闂佹寧鐟ㄩ? {ex.Message}");
        }
    }

    private async void RatingDebounceTimer_Tick(object? sender, object e)
    {
        _ratingDebounceTimer.Stop();
        
        if (_pendingRatingUpdate.HasValue)
        {
            var (image, rating) = _pendingRatingUpdate.Value;
            _pendingRatingUpdate = null;
            
            var allImagesToProcess = new List<ImageFileInfo>();
            
            if (_settingsService.AutoSyncGroupRatings && image.Group != null)
            {
                foreach (var groupImage in image.Group.Images)
                {
                    if (!allImagesToProcess.Contains(groupImage))
                    {
                        allImagesToProcess.Add(groupImage);
                    }
                }
            }
            else
            {
                allImagesToProcess.Add(image);
            }
            
            foreach (var imageInfo in allImagesToProcess)
            {
                await UpdateRatingAsync(imageInfo, rating);
                ViewModel.RefreshBurstCoverAfterRatingChanged(imageInfo);
            }
        }
    }

    private ImageFileInfo? FindImageInfoFromRatingControl(RatingControl ratingControl)
    {
        try
        {
            var parent = VisualTreeHelper.GetParent(ratingControl);
            while (parent != null)
            {
                if (parent is FrameworkElement fe && fe.DataContext is ImageFileInfo imageInfo)
                {
                    return imageInfo;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateRatingAsync(ImageFileInfo imageInfo, uint rating)
    {
        try
        {
            var ratingService = App.GetService<RatingService>();
            await imageInfo.SetRatingAsync(ratingService, rating);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateRatingAsync] 闂佹寧鐟ㄩ? {ex.Message}");
        }
    }




    private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        if (element.Tag is ImageFileInfo imageInfo)
        {
            _rightClickedImageInfo = imageInfo;
            if (!ImageGridView.SelectedItems.Contains(imageInfo))
            {
                ExecuteProgrammaticSelectionChange(() => ImageGridView.SelectedItem = imageInfo);
            }
        }

        ShowAttachedFlyoutAtPointer(element, e);
        e.Handled = true;
    }

    private void TreeViewItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not TreeViewItem treeViewItem)
            return;

        if (treeViewItem.Content is Grid grid && grid.Tag is FolderNode node)
        {
            _rightClickedFolderNode = node;
            ShowAttachedFlyoutAtPointer(grid, e);
        }
        e.Handled = true;
    }

    private void SubFolderItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        if (element.Tag is FolderNode node)
        {
            _rightClickedSubFolderNode = node;
            SubFolderGridView.SelectedItem = node;
            ShowAttachedFlyoutAtPointer(element, e);
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

    private FolderNode? _rightClickedFolderNode;
    private FolderNode? _rightClickedSubFolderNode;

    private void FolderTreeMenuFlyout_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            UpdateFolderPinMenuItems(flyout, _rightClickedFolderNode);
        }
    }

    private void SubFolderMenuFlyout_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            UpdateFolderPinMenuItems(flyout, _rightClickedSubFolderNode);
        }
    }

    private void UpdateFolderPinMenuItems(MenuFlyout flyout, FolderNode? node)
    {
        var hasFolderPath = !string.IsNullOrWhiteSpace(node?.FullPath);
        var isPinned = hasFolderPath && ViewModel.IsFolderPinned(node);
        var isExternalDeviceNode = IsNodeUnderExternalDevices(node);

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

    private async void PinSubFolder_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PinFolderAsync(_rightClickedSubFolderNode);
    }

    private async void UnpinSubFolder_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.UnpinFolderAsync(_rightClickedSubFolderNode);
    }

    private async void RefreshExternalDevices_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshExternalDevicesAsync();
    }

    private void OpenFolderInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolderNode == null || string.IsNullOrEmpty(_rightClickedFolderNode.FullPath))
        {
            return;
        }

        try
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _rightClickedFolderNode.FullPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(processInfo);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenFolderInExplorer_Click error: {ex}");
        }
    }

    private void AddFolderToPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedFolderNode == null || string.IsNullOrEmpty(_rightClickedFolderNode.FullPath))
            return;

        var previewWorkspace = App.GetService<PreviewWorkspaceService>();
        previewWorkspace.AddSource(_rightClickedFolderNode.FullPath);
    }

    private void OpenSubFolderInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedSubFolderNode == null || string.IsNullOrEmpty(_rightClickedSubFolderNode.FullPath))
        {
            return;
        }

        try
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _rightClickedSubFolderNode.FullPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(processInfo);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenSubFolderInExplorer_Click error: {ex}");
        }
    }

    private void AddSubFolderToPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedSubFolderNode == null || string.IsNullOrEmpty(_rightClickedSubFolderNode.FullPath))
            return;

        var previewWorkspace = App.GetService<PreviewWorkspaceService>();
        previewWorkspace.AddSource(_rightClickedSubFolderNode.FullPath);
    }

    private ImageFileInfo? _rightClickedImageInfo;

    private void OpenImageInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedImageInfo == null || _rightClickedImageInfo.ImageFile == null)
        {
            return;
        }

        try
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_rightClickedImageInfo.ImageFile.Path}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(processInfo);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenImageInExplorer_Click error: {ex}");
        }
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.GoBackAsync();
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.GoUpAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<IThumbnailService>().Clear();
        await ViewModel.RefreshAsync();
    }


    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ExportDialog(_settingsService, ViewModel.GetFilteredExportImages(), ViewModel.GetLoadedExportImages(), ViewModel.IsFilterActive);
        dialog.XamlRoot = XamlRoot;
        await dialog.ShowAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var pendingImages = ViewModel.GetPendingDeleteImages();
        if (pendingImages.Count == 0)
            return;

        var allExtensions = new HashSet<string>();
        foreach (var image in pendingImages)
        {
            if (image.ImageFile != null)
            {
                var ext = Path.GetExtension(image.ImageFile.Path).ToLowerInvariant();
                allExtensions.Add(ext);
            }
            if (image.HasAlternateFormats && image.AlternateFormats != null)
            {
                foreach (var altImage in image.AlternateFormats)
                {
                    if (altImage.ImageFile != null)
                    {
                        var altExt = Path.GetExtension(altImage.ImageFile.Path).ToLowerInvariant();
                        allExtensions.Add(altExt);
                    }
                }
            }
        }

        if (allExtensions.Count == 0)
            return;

        var dialog = new DeleteConfirmDialog(
            allExtensions.ToList(),
            pendingImages.Count,
            ViewModel.GetPendingDeleteBurstGroupCount())
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
                message => System.Diagnostics.Debug.WriteLine($"闁告帞濞€濞呭酣寮崶锔筋偨濠㈡儼绮剧憴? {message}"));

            RemoveDeletedImagesFromList(deleteResult.DeletedImages);
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var filesToDelete = GetFilesToDelete(pendingImages, dialog.SelectedExtensions);
            if (filesToDelete.Count == 0)
            {
                var emptyDialog = new ContentDialog
                {
                    Title = "MainPage_NoDeletableFilesTitle".GetLocalized(),
                    Content = "MainPage_NoDeletableFilesContent".GetLocalized(),
                    CloseButtonText = "Common_Ok".GetLocalized(),
                    XamlRoot = XamlRoot
                };
                await emptyDialog.ShowAsync();
                return;
            }

            var imagePathMap = DeleteWorkflowHelper.BuildPrimaryImagePathMap(pendingImages);
            var deleteResult = await DeleteWorkflowHelper.DeleteFilesAsync(
                filesToDelete,
                imagePathMap,
                dialog,
                App.GetService<IThumbnailService>(),
                message => System.Diagnostics.Debug.WriteLine($"闂佸憡甯炴繛鈧繛鍛叄瀵剟宕堕敂绛嬪仺婵犮垺鍎肩划鍓ф喆? {message}"));

            RemoveDeletedImagesFromList(deleteResult.DeletedImages);

            
            var deletedImages = new List<ImageFileInfo>();
            var thumbnailService = App.GetService<IThumbnailService>();
            var failedCount = 0;

            for (int i = 0; i < filesToDelete.Count; i++)
            {
                var file = filesToDelete[i];
                try
                {
                    await DeleteFileToRecycleBinAsync(file);
                    thumbnailService.Invalidate(file);
                    
                    var imageToDelete = pendingImages.FirstOrDefault(img => img.ImageFile?.Path == file.Path);
                    if (imageToDelete != null && !deletedImages.Contains(imageToDelete))
                    {
                        deletedImages.Add(imageToDelete);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"闁告帞濞€濞呭酣寮崶锔筋偨濠㈡儼绮剧憴? {file.Path}, 闂佹寧鐟ㄩ? {ex.Message}");
                    failedCount++;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    dialog.SetProgress(i + 1, filesToDelete.Count);
                });

                await System.Threading.Tasks.Task.Delay(10);
            }

            dialog.SetComplete();
            await System.Threading.Tasks.Task.Delay(500);

            RemoveDeletedImagesFromList(deletedImages);
        }
    }

    private List<StorageFile> GetFilesToDelete(List<ImageFileInfo> pendingImages, List<string> selectedExtensions)
    {
        return DeleteWorkflowHelper.GetFilesToDelete(pendingImages, selectedExtensions);
    }

    private async System.Threading.Tasks.Task DeleteFileToRecycleBinAsync(StorageFile file)
    {
        var settingsService = App.GetService<ISettingsService>();
        var useRecycleBin = settingsService.DeleteToRecycleBin;
        
        var isRemovableDrive = IsRemovableDrive(file.Path);
        
        if (isRemovableDrive)
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        else if (useRecycleBin)
        {
            await file.DeleteAsync(StorageDeleteOption.Default);
        }
        else
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
    }

    private static bool IsRemovableDrive(string filePath)
    {
        try
        {
            var driveLetter = Path.GetPathRoot(filePath);
            if (string.IsNullOrEmpty(driveLetter))
                return false;
            
            var driveInfo = new DriveInfo(driveLetter);
            return driveInfo.DriveType == DriveType.Removable;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveDeletedImagesFromList(List<ImageFileInfo> deletedImages)
    {
        if (deletedImages.Count == 0)
            return;

        var thumbnailService = App.GetService<IThumbnailService>();
        foreach (var deletedImage in deletedImages)
        {
            thumbnailService.Invalidate(deletedImage.ImageFile);
        }

        var firstVisibleIndex = GetFirstVisibleItemIndex();
        var selectedItem = ImageGridView.SelectedItem as ImageFileInfo;

        var batchedGroupsToProcess = new Dictionary<ImageGroup, List<ImageFileInfo>>();
        var batchedImagesToRemove = new List<ImageFileInfo>();
        var batchedReplacements = new List<KeyValuePair<ImageFileInfo, ImageFileInfo>>();

        foreach (var deletedImage in deletedImages)
        {
            if (deletedImage.Group != null)
            {
                if (!batchedGroupsToProcess.ContainsKey(deletedImage.Group))
                {
                    batchedGroupsToProcess[deletedImage.Group] = new List<ImageFileInfo>();
                }

                batchedGroupsToProcess[deletedImage.Group].Add(deletedImage);
            }
            else
            {
                batchedImagesToRemove.Add(deletedImage);
            }
        }

        foreach (var group in batchedGroupsToProcess.Keys)
        {
            var deletedInGroup = batchedGroupsToProcess[group];
            var oldPrimary = group.PrimaryImage;
            var remainingImages = group.Images.Where(img => !deletedInGroup.Contains(img)).ToList();

            if (remainingImages.Count > 0)
            {
                var newGroup = new ImageGroup(group.GroupName, remainingImages);
                var newPrimary = newGroup.PrimaryImage;
                newPrimary.Rating = oldPrimary.Rating;
                newPrimary.RatingSource = oldPrimary.RatingSource;
                newPrimary.IsRatingLoading = false;
                newPrimary.IsPendingDelete = false;
                newPrimary.RefreshGroupProperties();
                batchedReplacements.Add(new KeyValuePair<ImageFileInfo, ImageFileInfo>(oldPrimary, newPrimary));
            }
            else
            {
                batchedImagesToRemove.Add(oldPrimary);
            }
        }

        ViewModel.ApplyLibraryChanges(batchedImagesToRemove, batchedReplacements);
        ViewModel.ClearAllPendingDelete();

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (ViewModel.Images.Count > 0)
            {
                var indexToScroll = Math.Min(firstVisibleIndex, ViewModel.Images.Count - 1);
                if (indexToScroll >= 0)
                {
                    await ScrollItemIntoViewAsync(ViewModel.Images[indexToScroll], "delete-restore", ScrollIntoViewAlignment.Default);
                }

                if (selectedItem != null && deletedImages.Contains(selectedItem))
                {
                    var newSelectedIndex = Math.Min(firstVisibleIndex, ViewModel.Images.Count - 1);
                    if (newSelectedIndex >= 0)
                    {
                        ExecuteProgrammaticSelectionChange(() => ImageGridView.SelectedItem = ViewModel.Images[newSelectedIndex]);
                    }
                }
            }

            QueueVisibleThumbnailLoad("delete-restore");
        });
        return;
    }

    private int GetFirstVisibleItemIndex()
    {
        try
        {
            AttachImageItemsWrapGrid();
            if (_imageItemsWrapGrid != null &&
                _imageItemsWrapGrid.FirstVisibleIndex >= 0 &&
                _imageItemsWrapGrid.FirstVisibleIndex < ViewModel.Images.Count)
            {
                return _imageItemsWrapGrid.FirstVisibleIndex;
            }

            if (_thumbnailCoordinator.RealizedImageItems.Count > 0)
            {
                return _thumbnailCoordinator.RealizedImageItems
                    .Select(image => TryGetImageIndex(image, out var index) ? index : -1)
                    .Where(index => index >= 0)
                    .DefaultIfEmpty(0)
                    .Min();
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private void RebuildImageIndexMap()
    {
        _imageIndexMap.Clear();

        for (var i = 0; i < ViewModel.Images.Count; i++)
        {
            if (ViewModel.Images[i] is ImageFileInfo imageInfo)
            {
                _imageIndexMap[imageInfo] = i;
            }
        }
    }

    private bool TryGetImageIndex(ImageFileInfo imageInfo, out int index)
    {
        if (imageInfo != null && _imageIndexMap.TryGetValue(imageInfo, out index))
            return true;

        index = -1;
        return false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scrollViewer)
            return scrollViewer;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }

        return null;
    }

    private static ItemsWrapGrid? FindItemsWrapGrid(DependencyObject parent)
    {
        if (parent is ItemsWrapGrid itemsWrapGrid)
            return itemsWrapGrid;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var result = FindItemsWrapGrid(child);
            if (result != null)
                return result;
        }

        return null;
    }

    private void AttachImageItemsWrapGrid()
    {
        if (_imageItemsWrapGrid != null)
            return;

        _imageItemsWrapGrid = FindItemsWrapGrid(ImageGridView);
    }

    private void AttachImageGridScrollViewer()
    {
        if (_imageGridScrollViewer != null)
            return;

        _imageGridScrollViewer = FindScrollViewer(ImageGridView);
        if (_imageGridScrollViewer == null)
            return;

        _lastImageGridVerticalOffset = _imageGridScrollViewer.VerticalOffset;
        _imageGridScrollViewer.ViewChanging += ImageGridScrollViewer_ViewChanging;
        _imageGridScrollViewer.ViewChanged += ImageGridScrollViewer_ViewChanged;
    }

    private void DetachImageGridScrollViewer()
    {
        if (_imageGridScrollViewer == null)
            return;

        _imageGridScrollViewer.ViewChanging -= ImageGridScrollViewer_ViewChanging;
        _imageGridScrollViewer.ViewChanged -= ImageGridScrollViewer_ViewChanged;
        _imageGridScrollViewer = null;
    }

    private void AttachImageGridKeyDown()
    {
        if (_isImageGridKeyDownHandlerAttached)
            return;

        ImageGridView.AddHandler(UIElement.PreviewKeyDownEvent, _imageGridKeyDownHandler, true);
        _isImageGridKeyDownHandlerAttached = true;
    }

    private void DetachImageGridKeyDown()
    {
        if (!_isImageGridKeyDownHandlerAttached)
            return;

        ImageGridView.RemoveHandler(UIElement.PreviewKeyDownEvent, _imageGridKeyDownHandler);
        _isImageGridKeyDownHandlerAttached = false;
    }

    private void AttachImageGridPointerWheel()
    {
        if (_isImageGridPointerWheelHandlerAttached)
            return;

        ImageGridView.AddHandler(UIElement.PointerWheelChangedEvent, _imageGridPointerWheelHandler, true);
        _isImageGridPointerWheelHandlerAttached = true;
    }

    private void DetachImageGridPointerWheel()
    {
        if (!_isImageGridPointerWheelHandlerAttached)
            return;

        ImageGridView.RemoveHandler(UIElement.PointerWheelChangedEvent, _imageGridPointerWheelHandler);
        _isImageGridPointerWheelHandlerAttached = false;
    }


    private void ClearAllPendingDelete_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllPendingDelete();
    }
}








