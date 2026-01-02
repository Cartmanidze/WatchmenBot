namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Configuration options for profile processing pipeline
/// </summary>
public class ProfileOptions
{
    public int MaxMessagesPerBatch { get; set; } = 50;
    public int MinMessagesForFactExtraction { get; set; } = 3;
    public int MinMessagesForProfile { get; set; } = 10;
    public int ProfileSampleSize { get; set; } = 40;
    public int MaxFactsPerUser { get; set; } = 30;

    // Scheduling
    public int QueueProcessingIntervalMinutes { get; set; } = 15;
    public string NightlyProfileTime { get; set; } = "03:00";

    // Delays
    public int StartupDelaySeconds { get; set; } = 60;
    public int LlmRequestDelaySeconds { get; set; } = 2;
    public int ErrorRetryDelaySeconds { get; set; } = 300;

    public static ProfileOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("ProfileService");

        return new ProfileOptions
        {
            MaxMessagesPerBatch = section.GetValue("MaxMessagesPerBatch", 50),
            MinMessagesForFactExtraction = section.GetValue("MinMessagesForFactExtraction", 3),
            MinMessagesForProfile = section.GetValue("MinMessagesForProfile", 10),
            ProfileSampleSize = section.GetValue("ProfileSampleSize", 40),
            MaxFactsPerUser = section.GetValue("MaxFactsPerUser", 30),
            QueueProcessingIntervalMinutes = section.GetValue("QueueProcessingIntervalMinutes", 15),
            NightlyProfileTime = section.GetValue("NightlyProfileTime", "03:00"),
            StartupDelaySeconds = section.GetValue("StartupDelaySeconds", 60),
            LlmRequestDelaySeconds = section.GetValue("LlmRequestDelaySeconds", 2),
            ErrorRetryDelaySeconds = section.GetValue("ErrorRetryDelaySeconds", 300)
        };
    }
}
