namespace Vev.Atlas.Fabric.Portic;

/// <summary>
/// Configuration options for the Portic AI provider gateway. Read from the "Atlas:Portic" config section.
/// Portic handles provider authentication internally via environment variables configured in the gateway,
/// so no API key is required from the Atlas side. This makes Portic a key-free provider option for
/// Community Edition users who want to use the free daily AI allowance (3 questions/day).
/// </summary>
public sealed class PorticOptions
{
    /// <summary>The configuration section name: "Atlas:Portic".</summary>
    public const string SectionName = "Atlas:Portic";

    /// <summary>Base URL of the Portic gateway (e.g. <c>https://gateway.portic.example/v1</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Model identifier to use for completions. Defaults to <c>stub-echo</c> (Portic stub model).
    /// No API key is required — Portic manages provider credentials internally.
    /// </summary>
    public string? Model { get; set; } = "stub-echo";

    /// <summary>Maximum tokens for the completion response. Defaults to 1024.</summary>
    public int MaxTokens { get; set; } = 1024;
}
