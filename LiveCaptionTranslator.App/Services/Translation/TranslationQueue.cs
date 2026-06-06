using LiveCaptionTranslator.App.Models;
using LiveCaptionTranslator.App.Services.Subtitle;

namespace LiveCaptionTranslator.App.Services.Translation;

public sealed class TranslationQueue : IDisposable
{
    private readonly ITranslationService _translationService;
    private readonly object _syncRoot = new();
    private readonly Queue<TranslationWorkItem> _queue = new();
    private readonly Dictionary<string, TranslationResult> _cache = new(StringComparer.Ordinal);
    private readonly List<TranslationResult> _completedResults = [];
    private readonly HashSet<Guid> _supersededItemIds = [];
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _cancellationTokenSource;
    private TranslationWorkItem? _currentItem;
    private CancellationTokenSource? _currentItemCancellationTokenSource;
    private Task? _workerTask;

    public TranslationQueue(ITranslationService translationService)
    {
        _translationService = translationService;
    }

    public event EventHandler<TranslationResult>? TranslationCompleted;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? LogMessage;

    public int MaxQueueLength { get; init; } = 10;

    public TimeSpan TranslationTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public bool IsRunning => _workerTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        _workerTask = Task.Run(() => ProcessLoopAsync(_cancellationTokenSource.Token));
        PublishStatus("翻译队列运行中");
    }

    public async Task StopAsync()
    {
        if (_cancellationTokenSource is null)
        {
            PublishStatus("翻译队列已停止");
            return;
        }

        _cancellationTokenSource.Cancel();
        _signal.Release();

        try
        {
            if (_workerTask is not null)
            {
                await _workerTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            _workerTask = null;
            PublishStatus("翻译队列已停止");
        }
    }

    public void Enqueue(CaptionSegment segment, string sourceLanguage, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            return;
        }

        TranslationResult? cachedResult = null;
        var cacheKey = CreateCacheKey(segment.Text, sourceLanguage, targetLanguage);
        lock (_syncRoot)
        {
            if (_cache.TryGetValue(cacheKey, out cachedResult))
            {
                PublishStatus("命中翻译缓存");
            }
        }

        if (cachedResult is not null)
        {
            TranslationCompleted?.Invoke(this, cachedResult);
            return;
        }

        var shouldInvokeCachedResult = false;
        lock (_syncRoot)
        {
            if (HasMoreCompleteCompletedResult(segment.Text, sourceLanguage, targetLanguage, out var completedResult))
            {
                cachedResult = completedResult;
                shouldInvokeCachedResult = true;
            }
            else
            {
                RemoveSupersededCompletedResults(segment.Text, sourceLanguage, targetLanguage);
                RemoveSupersededQueuedItems(segment.Text, sourceLanguage, targetLanguage);
                CancelSupersededCurrentItem(segment.Text, sourceLanguage, targetLanguage);
            }

            if (cachedResult is not null)
            {
                PublishStatus("命中更完整翻译缓存");
            }
            else
            {
                if (_queue.Count >= MaxQueueLength)
                {
                    DropOldestShortPendingItem();
                }

                _queue.Enqueue(new TranslationWorkItem(segment, sourceLanguage, targetLanguage));
                PublishStatus(IsRunning
                    ? $"翻译队列等待中：{_queue.Count}"
                    : $"翻译队列已入队：{_queue.Count}，等待启动 worker");
            }
        }

        if (shouldInvokeCachedResult && cachedResult is not null)
        {
            TranslationCompleted?.Invoke(this, cachedResult);
            return;
        }

        _signal.Release();
    }

    public void ClearPending()
    {
        lock (_syncRoot)
        {
            _queue.Clear();
            _supersededItemIds.Clear();
        }
    }

    public void Supersede(
        CaptionSegment oldSegment,
        string replacementText,
        string sourceLanguage,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(replacementText) ||
            !SubtitleDeduplicator.AreSameUtterance(oldSegment.Text, replacementText) ||
            !SubtitleDeduplicator.IsMoreCompleteUtterance(replacementText, oldSegment.Text))
        {
            return;
        }

        lock (_syncRoot)
        {
            RemoveQueuedItem(oldSegment);

            if (_currentItem is not null &&
                _currentItem.Segment.Id == oldSegment.Id &&
                _currentItemCancellationTokenSource is not null)
            {
                _supersededItemIds.Add(_currentItem.Segment.Id);
                PublishLog($"取消被更完整原文替代的翻译任务：{oldSegment.Text}");
                _currentItemCancellationTokenSource.Cancel();
            }

            RemoveSupersededCompletedResults(replacementText, sourceLanguage, targetLanguage);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _signal.Dispose();
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            TranslationWorkItem? item;
            lock (_syncRoot)
            {
                item = _queue.Count > 0 ? _queue.Dequeue() : null;
                _currentItem = item;
            }

            if (item is null)
            {
                continue;
            }

            var cacheKey = CreateCacheKey(item.Segment.Text, item.SourceLanguage, item.TargetLanguage);
            if (_cache.TryGetValue(cacheKey, out var cachedResult))
            {
                TranslationCompleted?.Invoke(this, cachedResult);
                continue;
            }

            PublishStatus("正在翻译");

            TranslationResult result;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_syncRoot)
            {
                _currentItemCancellationTokenSource = timeoutCts;
            }

            timeoutCts.CancelAfter(TranslationTimeout);

            try
            {
                result = await _translationService.TranslateAsync(
                    item.Segment.Text,
                    item.SourceLanguage,
                    item.TargetLanguage,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (IsSuperseded(item))
                {
                    PublishStatus(GetQueueStatus());
                    ClearCurrentItem(item);
                    continue;
                }

                result = CreateTimeoutResult(item);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = CreateFailureResult(item, ex.Message);
            }

            if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                lock (_syncRoot)
                {
                    RemoveSupersededCompletedResults(result.SourceText, result.SourceLanguage, result.TargetLanguage);
                    _cache[cacheKey] = result;
                    _completedResults.Add(result);
                }
            }

            TranslationCompleted?.Invoke(this, result);
            PublishStatus(GetQueueStatus());

            ClearCurrentItem(item);
        }
    }

    private void RemoveQueuedItem(CaptionSegment segment)
    {
        if (_queue.Count == 0)
        {
            return;
        }

        var keptItems = new List<TranslationWorkItem>();
        foreach (var item in _queue)
        {
            if (item.Segment.Id == segment.Id)
            {
                _supersededItemIds.Add(item.Segment.Id);
                PublishLog($"移除被更完整原文替代的未处理翻译任务：{segment.Text}");
            }
            else
            {
                keptItems.Add(item);
            }
        }

        _queue.Clear();
        foreach (var item in keptItems)
        {
            _queue.Enqueue(item);
        }
    }

    private void RemoveSupersededQueuedItems(string newText, string sourceLanguage, string targetLanguage)
    {
        if (_queue.Count == 0)
        {
            return;
        }

        var keptItems = new List<TranslationWorkItem>();
        foreach (var item in _queue)
        {
            var shouldDrop =
                IsSameLanguagePair(item, sourceLanguage, targetLanguage) &&
                SubtitleDeduplicator.AreSameUtterance(item.Segment.Text, newText) &&
                SubtitleDeduplicator.IsMoreCompleteUtterance(newText, item.Segment.Text);

            if (shouldDrop)
            {
                _supersededItemIds.Add(item.Segment.Id);
                PublishLog($"取消旧的未处理翻译任务：{item.Segment.Text}");
            }
            else
            {
                keptItems.Add(item);
            }
        }

        _queue.Clear();
        foreach (var item in keptItems)
        {
            _queue.Enqueue(item);
        }
    }

    private void CancelSupersededCurrentItem(string newText, string sourceLanguage, string targetLanguage)
    {
        if (_currentItem is null || _currentItemCancellationTokenSource is null)
        {
            return;
        }

        if (!IsSameLanguagePair(_currentItem, sourceLanguage, targetLanguage) ||
            !SubtitleDeduplicator.AreSameUtterance(_currentItem.Segment.Text, newText) ||
            !SubtitleDeduplicator.IsMoreCompleteUtterance(newText, _currentItem.Segment.Text))
        {
            return;
        }

        PublishLog($"取消正在处理的旧翻译任务：{_currentItem.Segment.Text}");
        _supersededItemIds.Add(_currentItem.Segment.Id);
        _currentItemCancellationTokenSource.Cancel();
    }

    private bool IsSuperseded(TranslationWorkItem item)
    {
        lock (_syncRoot)
        {
            return _supersededItemIds.Remove(item.Segment.Id);
        }
    }

    private void ClearCurrentItem(TranslationWorkItem item)
    {
        lock (_syncRoot)
        {
            if (ReferenceEquals(_currentItem, item))
            {
                _currentItem = null;
                _currentItemCancellationTokenSource = null;
            }
        }
    }

    private bool HasMoreCompleteCompletedResult(
        string text,
        string sourceLanguage,
        string targetLanguage,
        out TranslationResult? result)
    {
        result = _completedResults.FirstOrDefault(item =>
            string.Equals(item.SourceLanguage, sourceLanguage, StringComparison.Ordinal) &&
            string.Equals(item.TargetLanguage, targetLanguage, StringComparison.Ordinal) &&
            SubtitleDeduplicator.AreSameUtterance(item.SourceText, text) &&
            !SubtitleDeduplicator.IsMoreCompleteUtterance(text, item.SourceText));

        return result is not null;
    }

    private void RemoveSupersededCompletedResults(string newText, string sourceLanguage, string targetLanguage)
    {
        var superseded = _completedResults
            .Where(item =>
                string.Equals(item.SourceLanguage, sourceLanguage, StringComparison.Ordinal) &&
                string.Equals(item.TargetLanguage, targetLanguage, StringComparison.Ordinal) &&
                SubtitleDeduplicator.AreSameUtterance(item.SourceText, newText) &&
                SubtitleDeduplicator.IsMoreCompleteUtterance(newText, item.SourceText))
            .ToList();

        foreach (var result in superseded)
        {
            _completedResults.Remove(result);
            _cache.Remove(CreateCacheKey(result.SourceText, result.SourceLanguage, result.TargetLanguage));
            PublishLog($"移除旧的已完成翻译缓存：{result.SourceText}");
        }
    }

    private void DropOldestShortPendingItem()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        var items = _queue.ToList();
        var dropIndex = items.FindIndex(item => item.Segment.Text.Length <= 20);
        if (dropIndex < 0)
        {
            dropIndex = 0;
        }

        var dropped = items[dropIndex];
        items.RemoveAt(dropIndex);

        _queue.Clear();
        foreach (var item in items)
        {
            _queue.Enqueue(item);
        }

        PublishLog($"翻译队列已满，丢弃旧片段：{dropped.Segment.Text}");
    }

    private string GetQueueStatus()
    {
        lock (_syncRoot)
        {
            return _queue.Count == 0 ? "翻译队列空闲" : $"翻译队列等待中：{_queue.Count}";
        }
    }

    private static string CreateCacheKey(string text, string sourceLanguage, string targetLanguage)
    {
        return $"{sourceLanguage}\u001f{targetLanguage}\u001f{text}";
    }

    private static TranslationResult CreateTimeoutResult(TranslationWorkItem item)
    {
        return new TranslationResult
        {
            SourceText = item.Segment.Text,
            SourceLanguage = item.SourceLanguage,
            TargetLanguage = item.TargetLanguage,
            CreatedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now,
            Status = "Timeout",
            ErrorMessage = "单句翻译超时。"
        };
    }

    private static TranslationResult CreateFailureResult(TranslationWorkItem item, string message)
    {
        return new TranslationResult
        {
            SourceText = item.Segment.Text,
            SourceLanguage = item.SourceLanguage,
            TargetLanguage = item.TargetLanguage,
            CreatedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now,
            Status = "Failed",
            ErrorMessage = message
        };
    }

    private void PublishStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void PublishLog(string message)
    {
        LogMessage?.Invoke(this, message);
    }

    private static bool IsSameLanguagePair(
        TranslationWorkItem item,
        string sourceLanguage,
        string targetLanguage)
    {
        return string.Equals(item.SourceLanguage, sourceLanguage, StringComparison.Ordinal) &&
               string.Equals(item.TargetLanguage, targetLanguage, StringComparison.Ordinal);
    }

    private sealed record TranslationWorkItem(
        CaptionSegment Segment,
        string SourceLanguage,
        string TargetLanguage);
}
