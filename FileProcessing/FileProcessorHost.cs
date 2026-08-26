using propseekr_file_processor;

namespace PropSeekr.FileProcessing;

/// <summary>Owns one reusable processor instance and its AWS/OpenAI clients.</summary>
public sealed class FileProcessorHost
{
    private readonly Lazy<Function> _processor = new(
        () => new Function(), LazyThreadSafetyMode.ExecutionAndPublication);

    public Function Processor => _processor.Value;
}
