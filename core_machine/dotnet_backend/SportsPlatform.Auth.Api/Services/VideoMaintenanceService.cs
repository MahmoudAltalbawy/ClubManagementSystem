namespace SportsPlatform.Auth.Api.Services;

public class VideoMaintenanceService : BackgroundService
{
    private static readonly HashSet<string> OptimizableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".m4v",
        ".mov",
    };

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<VideoMaintenanceService> _logger;
    private readonly Mp4StreamingOptimizer _optimizer;

    public VideoMaintenanceService(
        IWebHostEnvironment environment,
        ILogger<VideoMaintenanceService> logger,
        Mp4StreamingOptimizer optimizer)
    {
        _environment = environment;
        _logger = logger;
        _optimizer = optimizer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        if (string.IsNullOrWhiteSpace(_environment.WebRootPath))
            return;

        var uploadRoots = new[]
        {
            Path.Combine(_environment.WebRootPath, "uploads", "videos"),
            Path.Combine(_environment.WebRootPath, "uploads", "player-videos"),
            Path.Combine(_environment.WebRootPath, "uploads", "chat-media"),
        };

        var optimizedCount = 0;

        foreach (var root in uploadRoots)
        {
            if (stoppingToken.IsCancellationRequested)
                break;
            if (!Directory.Exists(root))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                if (!OptimizableExtensions.Contains(Path.GetExtension(filePath)))
                    continue;

                await _optimizer.OptimizeAsync(filePath);
                optimizedCount++;
            }
        }

        if (optimizedCount > 0)
            _logger.LogInformation("Checked {Count} uploaded video files for streaming optimization.", optimizedCount);
    }
}
