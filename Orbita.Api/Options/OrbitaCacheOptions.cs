namespace Orbita.Api.Options;

/// <summary>
/// Cache configuration. Redis is deliberately opt-in: a missing connection string
/// or a disabled flag always leaves PostgreSQL as the only read source.
/// </summary>
public sealed class OrbitaCacheOptions
{
    public const string SectionName = "Cache";

    public bool Enabled { get; set; }
    public string? RedisConfiguration { get; set; }
    public string InstanceName { get; set; } = "orbita:";
    public OrbitaCacheDomainOptions Domains { get; set; } = new();
}

public sealed class OrbitaCacheDomainOptions
{
    public bool Dashboard { get; set; } = true;
    public bool Responses { get; set; } = true;
    public bool Crm { get; set; } = true;
    public bool Analytics { get; set; } = true;
    public bool Reference { get; set; } = true;
    public bool Logs { get; set; } = true;
}
