using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ROSApp.Views;

public enum StorageProtocol
{
    S3,
    Sftp
}

public sealed partial class MainPage : Page
{
    private DispatcherQueue? _dispatcher;
    private SftpClient? _sftpClient;
    private AmazonS3Client? _client;
    private StorageProtocol _protocol = StorageProtocol.S3;
    private bool _uiReady;
    private bool _isConnected;
    private StorageProtocol _connectedProtocol = StorageProtocol.S3;
    private string _connectedEndpoint = string.Empty;
    private string? _connectedBucket;
    private string _objectsContextLabel = "未选择桶";
    private string _currentPrefix = "";
    private StorageFile? _uploadFile;
    private StorageFolder? _downloadFolder;
    private readonly string _settingsPath;
    private SettingsData _settings = new();
    private readonly List<ObjectItem> _allObjects = new();
    private readonly List<FolderItem> _allFolders = new();
    private bool _autoScrollLog = true;
    private bool _logWithTimestamp = true;
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private const string FixedUpdateFeedUrl = "http://ros.yuban.cloud/ros/latest.json";
    private string? _latestUpdateUrl;
    private string? _latestUpdateVersion;

    public ObservableCollection<BucketItem> Buckets { get; } = new();
    public ObservableCollection<ObjectItem> Objects { get; } = new();
    public ObservableCollection<FolderItem> Folders { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ROS-APP",
            "settings.json"
        );
        LoadSettings();

        BucketsList.ItemsSource = Buckets;
        ObjectsList.ItemsSource = Objects;
        FoldersList.ItemsSource = Folders;
        TransferBucketCombo.ItemsSource = Buckets;
        AboutVersion.Text = $"版本: {GetAppVersion()}";
        AboutAuthor.Text = "作者: Yuban-Network";
        AboutRuntime.Text = $".NET 运行时: {Environment.Version}";
        AboutProtocols.Text = "协议支持: S3 / SFTP";
        AboutSettingsPath.Text = $"配置路径: {_settingsPath}";
        AboutUpdateFeed.Text = $"更新源: {FixedUpdateFeedUrl}";
        UpdateStatusText.Text = "未检查更新";
        LoadLogPreferences();
        LoadUpdateSettings();
        LoadSavedKeys();
        LoadThemeMode();
        ApplyProtocolUiState();
        SetConnectionSummary("未连接", _protocol, "");
        SetTransferSummary("空闲");
        UpdateDashboardStats();
        _uiReady = true;
        ShowView(ViewLog);
    }

    private DispatcherQueue UiQueue => _dispatcher ??= DispatcherQueue.GetForCurrentThread();

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        AppendLog("点击连接。\n");
        var accessKey = AccessKeyBox.Text?.Trim();
        var secretKey = SecretKeyBox.Text?.Trim();
        var protocol = GetSelectedProtocol();
        var endpoint = protocol == StorageProtocol.S3
            ? (EndpointCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim()
            : (SftpEndpointCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim();

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            var tip = protocol == StorageProtocol.S3
                ? "请填写 Access Key 和 Secret Key。\n"
                : "请填写 SFTP 用户名和密码（Access Key / Secret Key）。\n";
            AppendLog(tip);
            return;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            AppendLog("请选择端点地址。\n");
            return;
        }

        SetBusy(true);
        _isConnected = false;
        SetConnectionSummary("连接中", protocol, endpoint);
        AppendLog("========================================\n");
        AppendLog($"开始连接 {GetProtocolText(protocol)}...\n");
        AppendLog($"端点地址: {endpoint}\n");

        try
        {
            _client?.Dispose();
            _client = null;
            if (_sftpClient is not null)
            {
                if (_sftpClient.IsConnected)
                {
                    _sftpClient.Disconnect();
                }
                _sftpClient.Dispose();
                _sftpClient = null;
            }

            if (protocol == StorageProtocol.S3)
            {
                var credentials = new BasicAWSCredentials(accessKey, secretKey);
                var config = new AmazonS3Config
                {
                    ServiceURL = endpoint,
                    ForcePathStyle = true
                };
                _client = new AmazonS3Client(credentials, config);
                _currentPrefix = "";
            }
            else
            {
                if (!TryParseSftpEndpoint(endpoint, out var host, out var port))
                {
                    AppendLog("SFTP 端点地址格式无效。\n");
                    return;
                }

                _sftpClient = await System.Threading.Tasks.Task.Run(() =>
                {
                    var client = new SftpClient(host, port, accessKey, secretKey);
                    client.Connect();
                    return client;
                });
                _currentPrefix = "/";
            }

            _protocol = protocol;
            _connectedProtocol = protocol;
            _connectedEndpoint = endpoint;
            _isConnected = true;
            SetConnectionSummary("已连接", _connectedProtocol, _connectedEndpoint);
            SaveKeysIfEnabled(accessKey, secretKey, protocol, endpoint);
            await LoadBucketsAsync();

            ConnectedDialog.Content = protocol == StorageProtocol.S3 ? "S3 已连接" : "SFTP 已连接";
            ConnectedDialog.XamlRoot = this.XamlRoot;
            await ConnectedDialog.ShowAsync();
            AppendLog("连接完成。\n");
            ShowView(ViewLog);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            SetConnectionSummary("连接失败", protocol, endpoint);
            AppendLog($"连接失败: {ex.Message}\n");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async System.Threading.Tasks.Task LoadBucketsAsync()
    {
        if (_protocol == StorageProtocol.S3 && _client is null)
        {
            AppendLog("未建立连接。\n");
            return;
        }
        if (_protocol == StorageProtocol.Sftp && (_sftpClient is null || !_sftpClient.IsConnected))
        {
            AppendLog("未建立 SFTP 连接。\n");
            return;
        }

        AppendLog("刷新桶列表...\n");
        Buckets.Clear();

        AppendLog("========================================\n");
        AppendLog("桶列表:\n");

        if (_protocol == StorageProtocol.S3)
        {
            var response = await _client!.ListBucketsAsync();
            if (response.Buckets.Count == 0)
            {
                AppendLog("  (无)\n");
                UpdateDashboardStats();
                return;
            }

            foreach (var b in response.Buckets)
            {
                var created = b.CreationDate.GetValueOrDefault();
                Buckets.Add(new BucketItem
                {
                    Name = b.BucketName,
                    CreatedAt = created.ToString("yyyy-MM-dd HH:mm")
                });
                AppendLog($"  - {b.BucketName}\n");
            }
            UpdateDashboardStats();
            return;
        }

        var sftpPath = "/";
        Buckets.Add(new BucketItem
        {
            Name = sftpPath,
            CreatedAt = "SFTP 根目录"
        });
        AppendLog("  - /\n");
        UpdateDashboardStats();
    }

    private async void OnBucketSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BucketsList.SelectedItem is not BucketItem item)
        {
            return;
        }

        _connectedBucket = item.Name;
        _currentPrefix = _protocol == StorageProtocol.S3 ? "" : "/";
        _objectsContextLabel = _protocol == StorageProtocol.S3 ? $"当前桶: {item.Name}" : "当前路径: /";
        if (ObjectsSearchBox is not null)
        {
            ObjectsSearchBox.Text = "";
        }
        UpdateObjectsHeader();
        AppendLog($"选择桶: {item.Name}\n");
        ShowView(ViewObjects);

        await LoadObjectsAsync(item.Name, _currentPrefix);
    }

    private async System.Threading.Tasks.Task LoadObjectsAsync(string bucketName, string prefix)
    {
        if (_protocol == StorageProtocol.S3)
        {
            await LoadS3ObjectsAsync(bucketName, prefix);
            return;
        }

        await LoadSftpObjectsAsync(prefix);
    }

    private async System.Threading.Tasks.Task LoadS3ObjectsAsync(string bucketName, string prefix)
    {
        if (_client is null)
        {
            AppendLog("未建立连接。\n");
            return;
        }

        try
        {
            AppendLog($"刷新对象列表: {bucketName} /{prefix}\n");
            _objectsContextLabel = $"当前桶: {bucketName}";
            Objects.Clear();
            Folders.Clear();
            _allObjects.Clear();
            _allFolders.Clear();
            if (PreviewTitle is not null) PreviewTitle.Text = "请选择对象";
            if (PreviewBox is not null) PreviewBox.Text = string.Empty;
            if (PathBox is not null) PathBox.Text = "/" + prefix;

            var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? continuation = null;
            do
            {
                AppendLog("请求 ListObjectsV2...\n");
                var req = new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    Prefix = prefix,
                    Delimiter = "/",
                    ContinuationToken = continuation
                };

                var resp = await _client.ListObjectsV2Async(req);
                if (resp is null)
                {
                    AppendLog("ListObjectsV2 返回空响应。\n");
                    return;
                }

                var commonPrefixes = resp.CommonPrefixes ?? new List<string>();
                var s3Objects = resp.S3Objects ?? new List<S3Object>();
                AppendLog($"ListObjectsV2: Objects={s3Objects.Count} CommonPrefixes={commonPrefixes.Count}\n");

                foreach (var folder in commonPrefixes)
                {
                    var name = folder;
                    if (name.StartsWith(prefix))
                    {
                        name = name.Substring(prefix.Length);
                    }
                    name = name.TrimEnd('/');
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        folderSet.Add(name + "/");
                    }
                }

                foreach (var obj in s3Objects)
                {
                    var key = obj.Key ?? string.Empty;
                    if (key == prefix || string.IsNullOrEmpty(key))
                    {
                        continue;
                    }
                    var remaining = key.StartsWith(prefix) ? key.Substring(prefix.Length) : key;
                    var slashIndex = remaining.IndexOf('/');
                    if (slashIndex >= 0)
                    {
                        var folderName = remaining.Substring(0, slashIndex + 1);
                        if (!string.IsNullOrWhiteSpace(folderName))
                        {
                            folderSet.Add(folderName);
                        }
                        continue;
                    }

                    var size = obj.Size ?? 0;
                    var modified = obj.LastModified.GetValueOrDefault();
                    Objects.Add(new ObjectItem
                    {
                        Key = remaining,
                        SizeText = FormatSize(size),
                        LastModified = modified.ToString("yyyy-MM-dd HH:mm"),
                        SizeBytes = size,
                        FullKey = key
                    });
                }

                continuation = resp.IsTruncated == true ? resp.NextContinuationToken : null;
            } while (continuation != null);

            foreach (var folderName in folderSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var prefixName = prefix + folderName;
                Folders.Add(new FolderItem { Name = folderName.TrimEnd('/'), Prefix = prefixName });
            }

            if (Folders.Count == 0 && Objects.Count == 0)
            {
                AppendLog("未获取到对象，尝试无分隔符模式...\n");
                await LoadObjectsFlatAsync(bucketName, prefix);
            }

            AppendLog($"对象统计: 文件夹 {Folders.Count}，文件 {Objects.Count}\n");
            CaptureObjectSnapshot();
            ApplyObjectFilter();
            UpdateDashboardStats();
        }
        catch (Exception ex)
        {
            AppendLog($"对象列表异常: {ex.Message}\n");
            AppendLog($"{ex}\n");
        }
    }

    private async System.Threading.Tasks.Task LoadSftpObjectsAsync(string prefix)
    {
        if (_sftpClient is null || !_sftpClient.IsConnected)
        {
            AppendLog("未建立 SFTP 连接。\n");
            return;
        }

        try
        {
            var normalized = NormalizeSftpDirectory(prefix);
            AppendLog($"刷新对象列表: sftp://{normalized}\n");
            _objectsContextLabel = $"当前路径: {normalized}";
            Objects.Clear();
            Folders.Clear();
            _allObjects.Clear();
            _allFolders.Clear();
            if (PreviewTitle is not null) PreviewTitle.Text = "请选择对象";
            if (PreviewBox is not null) PreviewBox.Text = string.Empty;
            if (PathBox is not null) PathBox.Text = normalized;

            var entries = await System.Threading.Tasks.Task.Run(() =>
            {
                return _sftpClient.ListDirectory(normalized).ToList();
            });

            foreach (var entry in entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Name == "." || entry.Name == "..")
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    Folders.Add(new FolderItem
                    {
                        Name = entry.Name,
                        Prefix = CombineSftpPath(normalized, entry.Name, true)
                    });
                    continue;
                }

                var fullPath = CombineSftpPath(normalized, entry.Name, false);
                Objects.Add(new ObjectItem
                {
                    Key = entry.Name,
                    SizeText = FormatSize((long)entry.Attributes.Size),
                    LastModified = entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    SizeBytes = (long)entry.Attributes.Size,
                    FullKey = fullPath
                });
            }

            AppendLog($"对象统计: 文件夹 {Folders.Count}，文件 {Objects.Count}\n");
            CaptureObjectSnapshot();
            ApplyObjectFilter();
            UpdateDashboardStats();
        }
        catch (Exception ex)
        {
            AppendLog($"SFTP 对象列表异常: {ex.Message}\n");
            AppendLog($"{ex}\n");
        }
    }

    private async System.Threading.Tasks.Task LoadObjectsFlatAsync(string bucketName, string prefix)
    {
        if (_client is null)
        {
            return;
        }

        Objects.Clear();
        string? continuation = null;
        do
        {
            var req = new ListObjectsV2Request
            {
                BucketName = bucketName,
                Prefix = prefix,
                ContinuationToken = continuation
            };

            var resp = await _client.ListObjectsV2Async(req);
            if (resp is null)
            {
                AppendLog("FlatList 返回空响应。\n");
                return;
            }

            var s3Objects = resp.S3Objects ?? new List<S3Object>();
            AppendLog($"FlatList: Objects={s3Objects.Count}\n");
            foreach (var obj in s3Objects)
            {
                var key = obj.Key ?? string.Empty;
                if (key == prefix || string.IsNullOrEmpty(key))
                {
                    continue;
                }
                var remaining = key.StartsWith(prefix) ? key.Substring(prefix.Length) : key;
                var size = obj.Size ?? 0;
                var modified = obj.LastModified.GetValueOrDefault();
                Objects.Add(new ObjectItem
                {
                    Key = remaining,
                    SizeText = FormatSize(size),
                    LastModified = modified.ToString("yyyy-MM-dd HH:mm"),
                    SizeBytes = size,
                    FullKey = key
                });
            }

            continuation = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (continuation != null);

        UpdateDashboardStats();
    }

    private async void OnFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FoldersList.SelectedItem is not FolderItem folder || _connectedBucket is null)
        {
            return;
        }

        _currentPrefix = folder.Prefix;
        AppendLog($"进入文件夹: {_currentPrefix}\n");
        await LoadObjectsAsync(_connectedBucket, _currentPrefix);
    }

    private async void OnGoUp(object sender, RoutedEventArgs e)
    {
        if (_connectedBucket is null)
        {
            return;
        }

        if (_protocol == StorageProtocol.Sftp)
        {
            _currentPrefix = GetSftpParentPath(_currentPrefix);
            AppendLog($"返回上一级: {_currentPrefix}\n");
            await LoadObjectsAsync(_connectedBucket, _currentPrefix);
            return;
        }

        if (string.IsNullOrEmpty(_currentPrefix))
        {
            return;
        }

        var trimmed = _currentPrefix.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        _currentPrefix = idx >= 0 ? trimmed.Substring(0, idx + 1) : "";
        AppendLog($"返回上一级: {_currentPrefix}\n");
        await LoadObjectsAsync(_connectedBucket, _currentPrefix);
    }

    private async void OnRefreshObjects(object sender, RoutedEventArgs e)
    {
        if (_connectedBucket is null)
        {
            return;
        }

        AppendLog("手动刷新对象视图。\n");
        await LoadObjectsAsync(_connectedBucket, _currentPrefix);
    }

    private void OnObjectsSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyObjectFilter();
    }

    private void OnObjectsSortChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyObjectFilter();
    }

    private void OnObjectsSortDescChanged(object sender, RoutedEventArgs e)
    {
        ApplyObjectFilter();
    }

    private void OnObjectsClearFilter(object sender, RoutedEventArgs e)
    {
        if (ObjectsSearchBox is not null)
        {
            ObjectsSearchBox.Text = string.Empty;
        }
        ApplyObjectFilter();
    }

    private void OnObjectItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ObjectItem obj)
        {
            return;
        }

        if (DownloadKeyBox is not null)
        {
            DownloadKeyBox.Text = obj.FullKey;
        }

        AppendLog($"已填充下载 Key: {obj.FullKey}\n");
    }

    private async void OnObjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ObjectsList.SelectedItem is not ObjectItem obj || _connectedBucket is null)
        {
            return;
        }

        PreviewTitle.Text = obj.Key;
        PreviewBox.Text = "加载中...";
        AppendLog($"预览对象: {obj.FullKey}\n");

        try
        {
            if (obj.SizeBytes > 1024 * 1024)
            {
                PreviewBox.Text = "对象较大，无法直接预览。";
                return;
            }

            string content;
            if (_protocol == StorageProtocol.S3)
            {
                if (_client is null)
                {
                    PreviewBox.Text = "预览失败：S3 未连接。";
                    return;
                }

                using var response = await _client.GetObjectAsync(_connectedBucket, obj.FullKey);
                using var reader = new StreamReader(response.ResponseStream);
                content = await reader.ReadToEndAsync();
            }
            else
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                {
                    PreviewBox.Text = "预览失败：SFTP 未连接。";
                    return;
                }

                content = await System.Threading.Tasks.Task.Run(() =>
                {
                    using var stream = _sftpClient.OpenRead(obj.FullKey);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    return reader.ReadToEnd();
                });
            }

            PreviewBox.Text = content;
        }
        catch (Exception ex)
        {
            PreviewBox.Text = $"预览失败: {ex.Message}";
        }
    }

    private async void OnObjectDownload(object sender, RoutedEventArgs e)
    {
        if (_connectedBucket is null)
        {
            AppendLog("下载失败：未建立连接或未选择桶。\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
            return;
        }

        var obj = ResolveObjectItem(sender);
        if (obj is null)
        {
            AppendLog("下载失败：未选择对象。\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
            return;
        }

        if (_downloadFolder is null)
        {
            var ok = await PickDownloadFolderAsync();
            if (!ok || _downloadFolder is null)
            {
                AppendLog("下载取消：未选择目录。\n");
                SetTransferSummary("下载取消");
                SetDownloadProgressMessage("进度：已取消");
                return;
            }
        }

        AppendLog($"右键下载: {_connectedBucket}/{obj.FullKey}\n");
        TransferStatus.Text = "右键下载中...";
        SetDownloadProgressInfo(0, 0, obj.SizeBytes > 0 ? obj.SizeBytes : null);
        SetTransferSummary("下载中");
        try
        {
            var fileName = System.IO.Path.GetFileName(obj.FullKey);
            var localPath = Path.Combine(_downloadFolder.Path, fileName);

            if (_protocol == StorageProtocol.S3)
            {
                if (_client is null)
                {
                    AppendLog("右键下载失败：S3 未连接。\n");
                    SetTransferSummary("下载失败");
                    SetDownloadProgressMessage("进度：失败");
                    return;
                }

                using var response = await _client.GetObjectAsync(_connectedBucket, obj.FullKey);
                await using var output = File.Create(localPath);
                var totalBytes = response.Headers.ContentLength > 0 ? response.Headers.ContentLength : (obj.SizeBytes > 0 ? obj.SizeBytes : 0);
                var buffer = new byte[1024 * 128];
                long transferred = 0;
                int read;
                while ((read = await response.ResponseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read);
                    transferred += read;
                    var percent = totalBytes > 0 ? Math.Min(100, transferred * 100.0 / totalBytes) : 0;
                    SetDownloadProgressInfo(percent, transferred, totalBytes > 0 ? totalBytes : null);
                    SetTransferStatus($"右键下载进度: {percent:F0}%");
                }
                if (totalBytes <= 0)
                {
                    SetDownloadProgressInfo(100);
                }
            }
            else
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                {
                    AppendLog("右键下载失败：SFTP 未连接。\n");
                    SetTransferSummary("下载失败");
                    SetDownloadProgressMessage("进度：失败");
                    return;
                }

                await System.Threading.Tasks.Task.Run(() =>
                {
                    long totalBytes = obj.SizeBytes > 0 ? obj.SizeBytes : 0;
                    using var output = File.Create(localPath);
                    _sftpClient.DownloadFile(obj.FullKey, output, downloaded =>
                    {
                        var transferred = downloaded > (ulong)long.MaxValue ? long.MaxValue : (long)downloaded;
                        var percent = totalBytes > 0 ? Math.Min(100, transferred * 100.0 / totalBytes) : 0;
                        SetDownloadProgressInfo(percent, transferred, totalBytes > 0 ? totalBytes : null);
                        SetTransferStatus($"右键下载进度: {percent:F0}%");
                    });
                });
            }

            SetDownloadProgressInfo(100);
            TransferStatus.Text = "右键下载完成。";
            AppendLog("右键下载完成。\n");
            SetTransferSummary("下载完成");
        }
        catch (Exception ex)
        {
            AppendLog($"右键下载失败: {ex.Message}\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
        }
    }

    private async void OnObjectDelete(object sender, RoutedEventArgs e)
    {
        if (_connectedBucket is null)
        {
            AppendLog("删除失败：未建立连接或未选择桶。\n");
            return;
        }

        var obj = ResolveObjectItem(sender);
        if (obj is null)
        {
            AppendLog("删除失败：未选择对象。\n");
            return;
        }

        var confirm = await ShowDeleteConfirmAsync(obj.FullKey);
        if (!confirm)
        {
            AppendLog("删除已取消。\n");
            return;
        }

        AppendLog($"右键删除: {_connectedBucket}/{obj.FullKey}\n");
        try
        {
            if (_protocol == StorageProtocol.S3)
            {
                if (_client is null)
                {
                    AppendLog("删除失败：S3 未连接。\n");
                    return;
                }
                await _client.DeleteObjectAsync(_connectedBucket, obj.FullKey);
            }
            else
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                {
                    AppendLog("删除失败：SFTP 未连接。\n");
                    return;
                }

                await System.Threading.Tasks.Task.Run(() =>
                {
                    _sftpClient.DeleteFile(obj.FullKey);
                });
            }

            AppendLog("删除完成。\n");
            await LoadObjectsAsync(_connectedBucket, _currentPrefix);
            UpdateDashboardStats();
        }
        catch (Exception ex)
        {
            AppendLog($"删除失败: {ex.Message}\n");
        }
    }

    private void OnObjectCopyKey(object sender, RoutedEventArgs e)
    {
        var obj = ResolveObjectItem(sender);
        if (obj is null)
        {
            AppendLog("复制失败：未选择对象。\n");
            return;
        }

        CopyToClipboard(obj.FullKey);
        AppendLog($"已复制对象 Key: {obj.FullKey}\n");
    }

    private void OnObjectCopyPath(object sender, RoutedEventArgs e)
    {
        var obj = ResolveObjectItem(sender);
        if (obj is null)
        {
            AppendLog("复制失败：未选择对象。\n");
            return;
        }

        string fullPath;
        if (_protocol == StorageProtocol.S3)
        {
            var bucket = _connectedBucket ?? "";
            fullPath = $"s3://{bucket}/{obj.FullKey}";
        }
        else
        {
            var endpoint = string.IsNullOrWhiteSpace(_connectedEndpoint)
                ? "sftp://"
                : _connectedEndpoint.TrimEnd('/');
            var objPath = obj.FullKey.StartsWith('/') ? obj.FullKey : "/" + obj.FullKey;
            fullPath = $"{endpoint}{objPath}";
        }

        CopyToClipboard(fullPath);
        AppendLog($"已复制完整路径: {fullPath}\n");
    }

    private async void OnPickUploadFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        _uploadFile = await picker.PickSingleFileAsync();
        UploadFileLabel.Text = _uploadFile?.Name ?? "未选择文件";
        SetUploadProgressInfo(0);
        AppendLog("选择上传文件。\n");
    }

    private async void OnPickDownloadFolder(object sender, RoutedEventArgs e)
    {
        await PickDownloadFolderAsync();
    }

    private async System.Threading.Tasks.Task<bool> PickDownloadFolderAsync()
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        _downloadFolder = await picker.PickSingleFolderAsync();
        DownloadFolderLabel.Text = _downloadFolder?.Path ?? "未选择目录";
        AppendLog("选择下载目录。\n");
        return _downloadFolder is not null;
    }

    private async void OnUploadClicked(object sender, RoutedEventArgs e)
    {
        if (!HasActiveConnection())
        {
            TransferStatus.Text = "未建立连接。";
            AppendLog("上传失败：未建立连接。\n");
            SetTransferSummary("上传失败");
            SetUploadProgressMessage("进度：失败");
            return;
        }

        if (TransferBucketCombo.SelectedItem is not BucketItem bucket)
        {
            TransferStatus.Text = "请选择目标桶。";
            AppendLog("上传失败：未选择桶。\n");
            SetTransferSummary("上传失败");
            SetUploadProgressMessage("进度：失败");
            return;
        }

        if (_uploadFile is null)
        {
            TransferStatus.Text = "请选择上传文件。";
            AppendLog("上传失败：未选择文件。\n");
            SetTransferSummary("上传失败");
            SetUploadProgressMessage("进度：失败");
            return;
        }

        var keyInput = TransferKeyBox.Text?.Trim() ?? string.Empty;
        var key = ResolveUploadTargetKey(keyInput, _uploadFile.Name);
        if (string.IsNullOrWhiteSpace(key))
        {
            TransferStatus.Text = "上传目标路径无效。";
            AppendLog("上传失败：目标路径无效。\n");
            SetTransferSummary("上传失败");
            SetUploadProgressMessage("进度：失败");
            return;
        }
        var localTotalBytes = new FileInfo(_uploadFile.Path).Length;
        TransferStatus.Text = "上传中...";
        SetUploadProgressInfo(0, 0, localTotalBytes);
        SetTransferSummary("上传中");
        AppendLog($"开始上传: {bucket.Name}/{key}\n");

        try
        {
            if (_protocol == StorageProtocol.S3)
            {
                if (_client is null)
                {
                    TransferStatus.Text = "S3 未建立连接。";
                    AppendLog("上传失败：S3 未连接。\n");
                    SetTransferSummary("上传失败");
                    SetUploadProgressMessage("进度：失败");
                    return;
                }

                var transfer = new TransferUtility(_client);
                var request = new TransferUtilityUploadRequest
                {
                    BucketName = bucket.Name,
                    Key = key,
                    FilePath = _uploadFile.Path
                };
                request.UploadProgressEvent += (_, args) =>
                {
                    var (transferred, total) = TryGetTransferBytes(args);
                    SetUploadProgressInfo(args.PercentDone, transferred, total ?? localTotalBytes);
                    SetTransferStatus($"上传进度: {args.PercentDone}%");
                };

                await transfer.UploadAsync(request);
            }
            else
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                {
                    TransferStatus.Text = "SFTP 未建立连接。";
                    AppendLog("上传失败：SFTP 未连接。\n");
                    SetTransferSummary("上传失败");
                    SetUploadProgressMessage("进度：失败");
                    return;
                }

                var remotePath = ResolveSftpRemotePath(key);
                var remoteDir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(remoteDir))
                {
                    await System.Threading.Tasks.Task.Run(() => EnsureSftpDirectoryExists(remoteDir));
                }

                var totalBytes = localTotalBytes;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var input = File.OpenRead(_uploadFile.Path);
                    _sftpClient.UploadFile(input, remotePath, uploaded =>
                    {
                        var percent = totalBytes <= 0 ? 100 : Math.Min(100, uploaded * 100.0 / totalBytes);
                        var transferred = uploaded > (ulong)long.MaxValue ? long.MaxValue : (long)uploaded;
                        SetUploadProgressInfo(percent, transferred, totalBytes);
                        SetTransferStatus($"上传进度: {percent:F0}%");
                    });
                });
            }

            TransferStatus.Text = "上传完成。";
            SetUploadProgressInfo(100, localTotalBytes, localTotalBytes);
            AppendLog("上传完成。\n");
            SetTransferSummary("上传完成");
            await ShowTransferDialogAsync("上传完成", $"{bucket.Name}/{key}");

            if (_connectedBucket == bucket.Name)
            {
                await LoadObjectsAsync(bucket.Name, _currentPrefix);
            }
        }
        catch (Exception ex)
        {
            TransferStatus.Text = $"上传失败: {ex.Message}";
            AppendLog($"上传失败: {ex.Message}\n");
            SetTransferSummary("上传失败");
            SetUploadProgressMessage("进度：失败");
        }
    }

    private async void OnDownloadClicked(object sender, RoutedEventArgs e)
    {
        if (!HasActiveConnection())
        {
            TransferStatus.Text = "未建立连接。";
            AppendLog("下载失败：未建立连接。\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
            return;
        }

        if (TransferBucketCombo.SelectedItem is not BucketItem bucket)
        {
            TransferStatus.Text = "请选择目标桶。";
            AppendLog("下载失败：未选择桶。\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
            return;
        }

        var key = DownloadKeyBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            TransferStatus.Text = "请输入下载对象 Key。";
            AppendLog("下载失败：未填写 Key。\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
            return;
        }

        if (_downloadFolder is null)
        {
            TransferStatus.Text = "请选择下载目录。";
            AppendLog("下载失败：未选择目录。\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
            return;
        }

        TransferStatus.Text = "下载中...";
        SetDownloadProgressInfo(0);
        SetTransferSummary("下载中");
        AppendLog($"开始下载: {bucket.Name}/{key}\n");

        try
        {
            var remoteKey = _protocol == StorageProtocol.S3 ? key : ResolveSftpRemotePath(key);
            var fileName = System.IO.Path.GetFileName(remoteKey);
            var localPath = Path.Combine(_downloadFolder.Path, fileName);

            if (_protocol == StorageProtocol.S3)
            {
                if (_client is null)
                {
                    TransferStatus.Text = "S3 未建立连接。";
                    AppendLog("下载失败：S3 未连接。\n");
                    SetTransferSummary("下载失败");
                    SetDownloadProgressMessage("进度：失败");
                    return;
                }

                var transfer = new TransferUtility(_client);
                var request = new TransferUtilityDownloadRequest
                {
                    BucketName = bucket.Name,
                    Key = remoteKey,
                    FilePath = localPath
                };
                request.WriteObjectProgressEvent += (_, args) =>
                {
                    var (transferred, total) = TryGetTransferBytes(args);
                    SetDownloadProgressInfo(args.PercentDone, transferred, total);
                    SetTransferStatus($"下载进度: {args.PercentDone}%");
                };

                await transfer.DownloadAsync(request);
            }
            else
            {
                if (_sftpClient is null || !_sftpClient.IsConnected)
                {
                    TransferStatus.Text = "SFTP 未建立连接。";
                    AppendLog("下载失败：SFTP 未连接。\n");
                    SetTransferSummary("下载失败");
                    SetDownloadProgressMessage("进度：失败");
                    return;
                }

                var attrs = await System.Threading.Tasks.Task.Run(() => _sftpClient.GetAttributes(remoteKey));
                var totalBytesLong = attrs.Size < 0 ? 0 : attrs.Size;
                var totalBytes = (double)totalBytesLong;

                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var output = File.Create(localPath);
                    _sftpClient.DownloadFile(remoteKey, output, downloaded =>
                    {
                        var percent = totalBytes <= 0 ? 100 : Math.Min(100, downloaded * 100.0 / totalBytes);
                        var transferred = downloaded > (ulong)long.MaxValue ? long.MaxValue : (long)downloaded;
                        SetDownloadProgressInfo(percent, transferred, totalBytesLong);
                        SetTransferStatus($"下载进度: {percent:F0}%");
                    });
                });
            }

            TransferStatus.Text = "下载完成。";
            SetDownloadProgressInfo(100);
            AppendLog("下载完成。\n");
            SetTransferSummary("下载完成");
            await ShowTransferDialogAsync("下载完成", $"{bucket.Name}/{remoteKey}");
        }
        catch (Exception ex)
        {
            TransferStatus.Text = $"下载失败: {ex.Message}";
            AppendLog($"下载失败: {ex.Message}\n");
            SetTransferSummary("下载失败");
            SetDownloadProgressMessage("进度：失败");
        }
    }

    private static void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow!);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    private static string GetAppVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        if (ver is null) return "0.0.0";
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    private static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} B";
        double kb = size / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F1} MB";
        double gb = mb / 1024.0;
        return $"{gb:F1} GB";
    }

    private void CaptureObjectSnapshot()
    {
        _allFolders.Clear();
        _allFolders.AddRange(Folders.Select(x => new FolderItem
        {
            Name = x.Name,
            Prefix = x.Prefix
        }));

        _allObjects.Clear();
        _allObjects.AddRange(Objects.Select(x => new ObjectItem
        {
            Key = x.Key,
            SizeText = x.SizeText,
            LastModified = x.LastModified,
            SizeBytes = x.SizeBytes,
            FullKey = x.FullKey
        }));
    }

    private void ApplyObjectFilter()
    {
        if (_allObjects.Count == 0 && _allFolders.Count == 0)
        {
            UpdateObjectsHeader();
            UpdateDashboardStats();
            return;
        }

        var keyword = ObjectsSearchBox?.Text?.Trim() ?? "";
        IEnumerable<FolderItem> folders = _allFolders;
        IEnumerable<ObjectItem> objects = _allObjects;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            folders = folders.Where(x => x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            objects = objects.Where(x => x.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var sortBy = (ObjectsSortCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "名称";
        var desc = ObjectsSortDescCheck?.IsChecked == true;

        folders = desc
            ? folders.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            : folders.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

        objects = sortBy switch
        {
            "大小" => desc
                ? objects.OrderByDescending(x => x.SizeBytes).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                : objects.OrderBy(x => x.SizeBytes).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase),
            "更新时间" => desc
                ? objects.OrderByDescending(x => x.LastModified, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                : objects.OrderBy(x => x.LastModified, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase),
            _ => desc
                ? objects.OrderByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase)
                : objects.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
        };

        Folders.Clear();
        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        Objects.Clear();
        foreach (var obj in objects)
        {
            Objects.Add(obj);
        }

        UpdateObjectsHeader();
        UpdateDashboardStats();
    }

    private void UpdateObjectsHeader()
    {
        if (ObjectsBucketLabel is null)
        {
            return;
        }

        var folderText = _allFolders.Count == Folders.Count
            ? Folders.Count.ToString()
            : $"{Folders.Count}/{_allFolders.Count}";
        var fileText = _allObjects.Count == Objects.Count
            ? Objects.Count.ToString()
            : $"{Objects.Count}/{_allObjects.Count}";
        ObjectsBucketLabel.Text = $"{_objectsContextLabel}  (文件夹 {folderText} / 文件 {fileText})";
    }

    private bool HasActiveConnection()
    {
        if (_protocol == StorageProtocol.S3)
        {
            return _client is not null;
        }

        return _sftpClient is not null && _sftpClient.IsConnected;
    }

    private StorageProtocol GetSelectedProtocol()
    {
        var text = (ProtocolCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.Equals(text, "SFTP", StringComparison.OrdinalIgnoreCase))
        {
            return StorageProtocol.Sftp;
        }

        return StorageProtocol.S3;
    }

    private static string GetProtocolText(StorageProtocol protocol)
    {
        return protocol == StorageProtocol.S3 ? "S3" : "SFTP";
    }

    private static bool TryParseNormalizedVersion(string raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(1);
        }

        var separatorIndex = cleaned.IndexOfAny(new[] { '-', '+' });
        if (separatorIndex > 0)
        {
            cleaned = cleaned.Substring(0, separatorIndex);
        }

        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            cleaned += ".0";
        }

        var parsed = Version.TryParse(cleaned, out var parsedVersion);
        version = parsedVersion ?? new Version(0, 0, 0);
        return parsed;
    }

    private static int CompareVersion(string left, string right)
    {
        if (TryParseNormalizedVersion(left, out var lv) && TryParseNormalizedVersion(right, out var rv))
        {
            return lv.CompareTo(rv);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private sealed class UpdateInfo
    {
        public string Version { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
    }

    private static UpdateInfo ParseUpdateInfo(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var version = GetJsonString(root, "version")
            ?? GetJsonString(root, "latestVersion")
            ?? GetJsonString(root, "tag_name")
            ?? string.Empty;
        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            version = version.Substring(1);
        }

        var notes = GetJsonString(root, "notes")
            ?? GetJsonString(root, "body")
            ?? string.Empty;

        var downloadUrl = GetJsonString(root, "downloadUrl")
            ?? GetJsonString(root, "url")
            ?? GetJsonString(root, "html_url")
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(downloadUrl)
            && root.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var candidate = GetJsonString(asset, "browser_download_url");
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    downloadUrl = candidate;
                    break;
                }
            }
        }

        return new UpdateInfo
        {
            Version = version.Trim(),
            DownloadUrl = downloadUrl.Trim(),
            Notes = notes.Trim()
        };
    }

    private static string NormalizeThemeMode(string? mode)
    {
        return string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
    }

    private string GetSelectedThemeMode()
    {
        var text = (ThemeModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        return text.Contains("深色", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
    }

    private void LoadThemeMode()
    {
        var mode = NormalizeThemeMode(_settings.ThemeMode);
        _settings.ThemeMode = mode;
        if (ThemeModeCombo is not null)
        {
            SelectComboByContent(ThemeModeCombo, mode == "Dark" ? "深色模式" : "浅色模式");
        }
        ApplyThemeMode(mode, false);
    }

    private void ApplyThemeMode(string mode, bool writeLog)
    {
        var normalized = NormalizeThemeMode(mode);
        RequestedTheme = normalized == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
        if (writeLog)
        {
            AppendLog(normalized == "Dark" ? "主题已切换为深色模式。\n" : "主题已切换为浅色模式。\n");
        }
    }

    private string GetSelectedEndpoint(StorageProtocol protocol)
    {
        return protocol == StorageProtocol.S3
            ? (EndpointCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? ""
            : (SftpEndpointCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "";
    }

    private void SetConnectionSummary(string status, StorageProtocol protocol, string endpoint)
    {
        if (ConnectionStatusText is null || ConnectionProtocolText is null || ConnectionEndpointText is null)
        {
            return;
        }

        ConnectionStatusText.Text = status;
        ConnectionProtocolText.Text = $"协议：{GetProtocolText(protocol)}";
        ConnectionEndpointText.Text = string.IsNullOrWhiteSpace(endpoint) ? "端点：-" : $"端点：{endpoint}";
    }

    private void ApplyProtocolUiState()
    {
        if (S3EndpointPanel is null || SftpEndpointPanel is null || EndpointLabel is null
            || AccessKeyLabel is null || SecretKeyLabel is null)
        {
            return;
        }

        var isSftp = _protocol == StorageProtocol.Sftp;
        S3EndpointPanel.Visibility = isSftp ? Visibility.Collapsed : Visibility.Visible;
        SftpEndpointPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        EndpointLabel.Text = "S3 端点地址";
        AccessKeyLabel.Text = isSftp ? "用户名（Access Key）" : "Access Key";
        SecretKeyLabel.Text = isSftp ? "密码（Secret Key）" : "Secret Key";
    }

    private static bool TryParseSftpEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 22;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        port = uri.Port > 0 ? uri.Port : 22;
        return true;
    }

    private static string NormalizeSftpDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("/"))
        {
            normalized = "/" + normalized;
        }
        if (!normalized.EndsWith("/"))
        {
            normalized += "/";
        }
        return normalized;
    }

    private static string CombineSftpPath(string directory, string name, bool asDirectory)
    {
        var dir = NormalizeSftpDirectory(directory).TrimEnd('/');
        var segment = (name ?? string.Empty).Trim('/');
        var combined = string.IsNullOrWhiteSpace(segment) ? dir : $"{dir}/{segment}";
        if (string.IsNullOrEmpty(combined))
        {
            combined = "/";
        }
        if (asDirectory && !combined.EndsWith("/"))
        {
            combined += "/";
        }
        return combined;
    }

    private static string GetSftpParentPath(string current)
    {
        var normalized = NormalizeSftpDirectory(current);
        if (normalized == "/")
        {
            return "/";
        }

        var trimmed = normalized.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx <= 0)
        {
            return "/";
        }

        return trimmed.Substring(0, idx + 1);
    }

    private string ResolveSftpRemotePath(string key)
    {
        if (key.StartsWith("/"))
        {
            return key.Replace('\\', '/');
        }

        var baseDir = NormalizeSftpDirectory(_currentPrefix);
        return CombineSftpPath(baseDir, key, false);
    }

    private string ResolveUploadTargetKey(string keyInput, string fileName)
    {
        var normalizedFileName = (fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            return string.Empty;
        }

        var input = (keyInput ?? string.Empty).Trim().Replace('\\', '/');
        var treatAsDirectory = string.IsNullOrWhiteSpace(input) || input.EndsWith("/");

        if (_protocol == StorageProtocol.S3)
        {
            // S3 key is always relative path-style text without leading slash.
            var basePrefix = (_currentPrefix ?? string.Empty).Replace('\\', '/').Trim();
            if (!string.IsNullOrWhiteSpace(basePrefix) && !basePrefix.EndsWith("/"))
            {
                basePrefix += "/";
            }

            var cleanedInput = input.TrimStart('/');
            if (treatAsDirectory)
            {
                var dirPrefix = cleanedInput;
                if (string.IsNullOrWhiteSpace(dirPrefix))
                {
                    dirPrefix = basePrefix;
                }
                if (!string.IsNullOrWhiteSpace(dirPrefix) && !dirPrefix.EndsWith("/"))
                {
                    dirPrefix += "/";
                }
                return $"{dirPrefix}{normalizedFileName}";
            }

            return cleanedInput;
        }

        if (treatAsDirectory)
        {
            var dirInput = input;
            if (string.IsNullOrWhiteSpace(dirInput))
            {
                dirInput = _currentPrefix;
            }
            if (!string.IsNullOrWhiteSpace(dirInput) && !dirInput.EndsWith("/"))
            {
                dirInput += "/";
            }
            var combined = $"{dirInput}{normalizedFileName}";
            return ResolveSftpRemotePath(combined);
        }

        return ResolveSftpRemotePath(input);
    }

    private void EnsureSftpDirectoryExists(string directoryPath)
    {
        if (_sftpClient is null || !_sftpClient.IsConnected)
        {
            return;
        }

        var normalized = NormalizeSftpDirectory(directoryPath);
        if (normalized == "/")
        {
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "/";
        foreach (var part in parts)
        {
            current = current == "/" ? "/" + part : current + "/" + part;
            if (!_sftpClient.Exists(current))
            {
                _sftpClient.CreateDirectory(current);
            }
        }
    }

    private static void SelectComboByContent(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi
                && string.Equals(cbi.Content?.ToString()?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        LogBox.Text = string.Empty;
        AppendLog("日志已清空。\n");
    }

    private void OnCopyLogClicked(object sender, RoutedEventArgs e)
    {
        var text = LogBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            AppendLog("复制日志失败：日志为空。\n");
            return;
        }

        CopyToClipboard(text);
        AppendLog("已复制日志到剪贴板。\n");
    }

    private void OnAutoScrollLogChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _autoScrollLog = AutoScrollLogCheck?.IsChecked == true;
        _settings.AutoScrollLog = _autoScrollLog;
        SaveSettings();
        AppendLog(_autoScrollLog ? "日志自动滚动已开启。\n" : "日志自动滚动已关闭。\n");
    }

    private void OnTimestampLogChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _logWithTimestamp = TimestampLogCheck?.IsChecked == true;
        _settings.LogWithTimestamp = _logWithTimestamp;
        SaveSettings();
        AppendLog(_logWithTimestamp ? "日志时间戳已开启。\n" : "日志时间戳已关闭。\n");
    }

    private void OnSaveUpdateFeedUrl(object sender, RoutedEventArgs e)
    {
        AppendLog("更新地址已固定，无需配置。\n");
    }

    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        var feedUrl = FixedUpdateFeedUrl;

        CheckUpdateButton.IsEnabled = false;
        OpenUpdateUrlButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新...";
        UpdateNotesText.Text = string.Empty;
        AppendLog($"开始检查更新: {feedUrl}\n");

        try
        {
            var payload = await _httpClient.GetStringAsync(feedUrl);
            var info = ParseUpdateInfo(payload);
            if (string.IsNullOrWhiteSpace(info.Version))
            {
                throw new InvalidOperationException("更新源缺少 version/tag_name 字段。");
            }

            var currentVersion = GetAppVersion();
            var compare = CompareVersion(info.Version, currentVersion);

            _latestUpdateVersion = info.Version;
            _latestUpdateUrl = info.DownloadUrl;
            UpdateNotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? string.Empty : $"更新说明：{info.Notes}";

            if (compare > 0)
            {
                UpdateStatusText.Text = $"发现新版本：{info.Version}（当前 {currentVersion}）";
                OpenUpdateUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(info.DownloadUrl);
                AppendLog($"发现新版本: {info.Version}\n");
            }
            else
            {
                UpdateStatusText.Text = $"当前已是最新版本：{currentVersion}";
                OpenUpdateUrlButton.IsEnabled = false;
                AppendLog("已是最新版本。\n");
            }
        }
        catch (Exception ex)
        {
            _latestUpdateUrl = null;
            _latestUpdateVersion = null;
            UpdateStatusText.Text = $"检查更新失败：{ex.Message}";
            UpdateNotesText.Text = string.Empty;
            OpenUpdateUrlButton.IsEnabled = false;
            AppendLog($"检查更新失败: {ex.Message}\n");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnOpenUpdateUrlClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestUpdateUrl))
        {
            AppendLog("打开下载页失败：当前没有可用下载地址。\n");
            return;
        }

        if (!Uri.TryCreate(_latestUpdateUrl, UriKind.Absolute, out var uri))
        {
            AppendLog($"打开下载页失败：URL 无效 ({_latestUpdateUrl})\n");
            return;
        }

        var ok = await Windows.System.Launcher.LaunchUriAsync(uri);
        if (!ok)
        {
            AppendLog("打开下载页失败：系统未处理该链接。\n");
            return;
        }

        AppendLog($"已打开下载页: {_latestUpdateUrl}\n");
    }

    private void OnNavLog(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到日志视图。\n");
        ShowView(ViewLog);
    }

    private void OnNavConnect(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到连接视图。\n");
        ShowView(ViewConnect);
    }

    private async void OnNavBuckets(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到桶列表视图。\n");
        ShowView(ViewBuckets);
        await LoadBucketsAsync();
    }

    private async void OnNavObjects(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到对象浏览视图。\n");
        ShowView(ViewObjects);
        if (_connectedBucket is not null)
        {
            await LoadObjectsAsync(_connectedBucket, _currentPrefix);
        }
        else
        {
            AppendLog("对象浏览：未选择桶。\n");
        }
    }

    private void OnNavTransfer(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到上传/下载视图。\n");
        ShowView(ViewTransfer);
    }

    private void OnNavSettings(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到设置视图。\n");
        ShowView(ViewSettings);
    }

    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        var mode = GetSelectedThemeMode();
        if (string.Equals(NormalizeThemeMode(_settings.ThemeMode), mode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ThemeMode = mode;
        SaveSettings();
        ApplyThemeMode(mode, true);
    }

    private void OnNavAbout(object sender, RoutedEventArgs e)
    {
        AppendLog("切换到关于视图。\n");
        ShowView(ViewAbout);
    }

    private void OnProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _protocol = GetSelectedProtocol();
        ApplyProtocolUiState();
        if (!_isConnected)
        {
            var endpoint = GetSelectedEndpoint(_protocol);
            SetConnectionSummary("未连接", _protocol, endpoint);
        }
        AppendLog($"连接协议切换为: {GetProtocolText(_protocol)}\n");
    }

    private void ShowView(UIElement target)
    {
        ViewLog.Visibility = Visibility.Collapsed;
        ViewConnect.Visibility = Visibility.Collapsed;
        ViewBuckets.Visibility = Visibility.Collapsed;
        ViewObjects.Visibility = Visibility.Collapsed;
        ViewTransfer.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Collapsed;
        ViewAbout.Visibility = Visibility.Collapsed;

        target.Visibility = Visibility.Visible;
    }

    private string FormatLogText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n");
        if (!_logWithTimestamp)
        {
            return normalized.Replace("\n", Environment.NewLine);
        }

        var lines = normalized.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append(Environment.NewLine);
                continue;
            }

            sb.Append('[');
            sb.Append(DateTime.Now.ToString("HH:mm:ss"));
            sb.Append("] ");
            sb.Append(line);
            sb.Append(Environment.NewLine);
        }

        return sb.ToString();
    }

    private void AppendLog(string text)
    {
        if (UiQueue.HasThreadAccess)
        {
            LogBox.Text += FormatLogText(text);
            if (_autoScrollLog)
            {
                LogBox.SelectionStart = LogBox.Text.Length;
            }
        }
        else
        {
            UiQueue.TryEnqueue(() => AppendLog(text));
        }
    }

    private void SetBusy(bool isBusy)
    {
        if (UiQueue.HasThreadAccess)
        {
            BusyRing.IsActive = isBusy;
            ConnectButton.IsEnabled = !isBusy;
        }
        else
        {
            UiQueue.TryEnqueue(() => SetBusy(isBusy));
        }
    }

    private void SetProgress(ProgressBar bar, double value)
    {
        if (UiQueue.HasThreadAccess)
        {
            bar.Value = value;
        }
        else
        {
            UiQueue.TryEnqueue(() => bar.Value = value);
        }
    }

    private static string FormatProgressText(double percent, long? transferredBytes = null, long? totalBytes = null)
    {
        var normalized = Math.Max(0, Math.Min(100, percent));
        if (transferredBytes.HasValue && totalBytes.HasValue && totalBytes.Value > 0)
        {
            return $"进度：{normalized:F0}% ({FormatSize(transferredBytes.Value)} / {FormatSize(totalBytes.Value)})";
        }

        return $"进度：{normalized:F0}%";
    }

    private static long? GetLongFromProperty(object source, string propertyName)
    {
        var prop = source.GetType().GetProperty(propertyName);
        if (prop is null)
        {
            return null;
        }

        var value = prop.GetValue(source);
        return value switch
        {
            long x => x,
            int x => x,
            uint x => x,
            ulong x when x <= long.MaxValue => (long)x,
            _ => null
        };
    }

    private static (long? transferred, long? total) TryGetTransferBytes(object args)
    {
        var transferred = GetLongFromProperty(args, "TransferredBytes")
            ?? GetLongFromProperty(args, "Transferred");
        var total = GetLongFromProperty(args, "TotalBytes")
            ?? GetLongFromProperty(args, "Total");
        return (transferred, total);
    }

    private void SetUploadProgressInfo(double percent, long? transferredBytes = null, long? totalBytes = null)
    {
        SetProgress(UploadProgress, percent);
        SetUploadProgressMessage(FormatProgressText(percent, transferredBytes, totalBytes));
    }

    private void SetUploadProgressMessage(string text)
    {
        if (UiQueue.HasThreadAccess)
        {
            if (UploadProgressText is not null)
            {
                UploadProgressText.Text = text;
            }
        }
        else
        {
            UiQueue.TryEnqueue(() => SetUploadProgressMessage(text));
        }
    }

    private void SetDownloadProgressInfo(double percent, long? transferredBytes = null, long? totalBytes = null)
    {
        SetProgress(DownloadProgress, percent);
        SetDownloadProgressMessage(FormatProgressText(percent, transferredBytes, totalBytes));
    }

    private void SetDownloadProgressMessage(string text)
    {
        if (UiQueue.HasThreadAccess)
        {
            if (DownloadProgressText is not null)
            {
                DownloadProgressText.Text = text;
            }
        }
        else
        {
            UiQueue.TryEnqueue(() => SetDownloadProgressMessage(text));
        }
    }

    private void SetTransferStatus(string text)
    {
        if (UiQueue.HasThreadAccess)
        {
            TransferStatus.Text = text;
        }
        else
        {
            UiQueue.TryEnqueue(() => TransferStatus.Text = text);
        }
    }

    private void SetTransferSummary(string text)
    {
        if (UiQueue.HasThreadAccess)
        {
            if (TransferSummaryText is not null)
            {
                TransferSummaryText.Text = text;
            }
        }
        else
        {
            UiQueue.TryEnqueue(() => SetTransferSummary(text));
        }
    }

    private void UpdateDashboardStats()
    {
        if (UiQueue.HasThreadAccess)
        {
            if (BucketsCountText is not null)
            {
                BucketsCountText.Text = Buckets.Count.ToString();
            }
            if (ObjectsCountText is not null)
            {
                ObjectsCountText.Text = $"{Folders.Count + Objects.Count} ({Folders.Count} 目录/{Objects.Count} 文件)";
            }
        }
        else
        {
            UiQueue.TryEnqueue(UpdateDashboardStats);
        }
    }

    private void LoadLogPreferences()
    {
        _autoScrollLog = _settings.AutoScrollLog ?? true;
        _logWithTimestamp = _settings.LogWithTimestamp ?? true;
        _settings.AutoScrollLog = _autoScrollLog;
        _settings.LogWithTimestamp = _logWithTimestamp;

        if (AutoScrollLogCheck is not null)
        {
            AutoScrollLogCheck.IsChecked = _autoScrollLog;
        }
        if (TimestampLogCheck is not null)
        {
            TimestampLogCheck.IsChecked = _logWithTimestamp;
        }
    }

    private void LoadUpdateSettings()
    {
        if (UpdateStatusText is not null)
        {
            UpdateStatusText.Text = "未检查更新";
        }
        if (UpdateNotesText is not null)
        {
            UpdateNotesText.Text = string.Empty;
        }
        if (OpenUpdateUrlButton is not null)
        {
            OpenUpdateUrlButton.IsEnabled = false;
        }
    }

    private async System.Threading.Tasks.Task<bool> ShowDeleteConfirmAsync(string key)
    {
        if (ConfirmDeleteDialog is null)
        {
            return true;
        }

        ConfirmDeleteDialog.Content = $"确认删除对象：{key}\n此操作不可恢复。";
        ConfirmDeleteDialog.XamlRoot = this.XamlRoot;
        var result = await ConfirmDeleteDialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async System.Threading.Tasks.Task ShowTransferDialogAsync(string title, string detail)
    {
        if (TransferDialog is null)
        {
            return;
        }

        TransferDialog.Title = title;
        TransferDialog.Content = $"任务已完成：{detail}";
        TransferDialog.XamlRoot = this.XamlRoot;
        await TransferDialog.ShowAsync();
    }

    private ObjectItem? ResolveObjectItem(object? sender)
    {
        if (sender is MenuFlyoutItem item && item.DataContext is ObjectItem data)
        {
            ObjectsList.SelectedItem = data;
            return data;
        }

        if (ObjectsList.SelectedItem is ObjectItem selected)
        {
            return selected;
        }

        return null;
    }

    private void LoadSavedKeys()
    {
        var useLast = _settings.UseLastKey;
        UseLastKeyCheck.IsChecked = useLast;

        var protocolText = string.IsNullOrWhiteSpace(_settings.LastProtocol) ? "S3" : _settings.LastProtocol!;
        SelectComboByContent(ProtocolCombo, protocolText);
        _protocol = GetSelectedProtocol();
        ApplyProtocolUiState();

        if (!string.IsNullOrWhiteSpace(_settings.LastS3Endpoint))
        {
            SelectComboByContent(EndpointCombo, _settings.LastS3Endpoint!);
        }
        if (!string.IsNullOrWhiteSpace(_settings.LastSftpEndpoint))
        {
            SelectComboByContent(SftpEndpointCombo, _settings.LastSftpEndpoint!);
        }

        if (useLast)
        {
            AccessKeyBox.Text = _settings.LastAccessKey ?? "";
            SecretKeyBox.Text = _settings.LastSecretKey ?? "";
            AppendLog("已加载上次 Key。\n");
        }
    }

    private void SaveKeysIfEnabled(string accessKey, string secretKey, StorageProtocol protocol, string endpoint)
    {
        var useLast = UseLastKeyCheck.IsChecked == true;
        _settings.UseLastKey = useLast;
        _settings.LastProtocol = GetProtocolText(protocol);
        if (protocol == StorageProtocol.S3)
        {
            _settings.LastS3Endpoint = endpoint;
        }
        else
        {
            _settings.LastSftpEndpoint = endpoint;
        }

        if (useLast)
        {
            _settings.LastAccessKey = accessKey;
            _settings.LastSecretKey = secretKey;
            AppendLog("已保存 Key。\n");
        }
        else
        {
            _settings.LastAccessKey = "";
            _settings.LastSecretKey = "";
        }
        SaveSettings();
    }

    private void OnClearSavedKey(object sender, RoutedEventArgs e)
    {
        _settings.LastAccessKey = "";
        _settings.LastSecretKey = "";
        _settings.UseLastKey = false;
        _settings.LastProtocol = "";
        _settings.LastS3Endpoint = "";
        _settings.LastSftpEndpoint = "";
        UseLastKeyCheck.IsChecked = false;
        SaveSettings();
        AppendLog("已清空保存的 Key。\n");
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var data = JsonSerializer.Deserialize<SettingsData>(json);
                if (data is not null)
                {
                    _settings = data;
                }
            }
        }
        catch
        {
            _settings = new SettingsData();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // ignore persistence errors
        }
    }
}

public sealed class BucketItem
{
    public string Name { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class ObjectItem
{
    public string Key { get; set; } = string.Empty;
    public string SizeText { get; set; } = string.Empty;
    public string LastModified { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FullKey { get; set; } = string.Empty;
}

public sealed class FolderItem
{
    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
}

public sealed class SettingsData
{
    public bool UseLastKey { get; set; }
    public string? LastAccessKey { get; set; }
    public string? LastSecretKey { get; set; }
    public string? LastProtocol { get; set; }
    public string? LastS3Endpoint { get; set; }
    public string? LastSftpEndpoint { get; set; }
    public string? ThemeMode { get; set; }
    public bool? AutoScrollLog { get; set; }
    public bool? LogWithTimestamp { get; set; }
    public string? UpdateFeedUrl { get; set; }
}
