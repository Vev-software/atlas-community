namespace Vev.Atlas.Fabric.Portic;

/// <summary>
/// Configuration options for the Portic AI provider gateway. Read from the "Atlas:Portic" config section.
/// </summary>
public sealed class PorticOptions
{
    /// <summary>The configuration section name: "Atlas:Portic".</summary>
    public const string SectionName = "Atlas:Portic";

    /// <summary>Base URL of the Portic gateway (e.g. <c>https://gateway.portic.example/v1</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Model identifier to use for completions (e.g. <c>gpt-4.1-mini</c>).</summary>
    public string? Model { get; set; }

    /// <summary>Maximum tokens for the completion response. Defaults to 1024.</summary>
    public int MaxTokens { get; set; } = 1024;
}
