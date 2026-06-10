namespace terraform.Engine.Configs;

public class EngineOptions
{
    public int ReactorCount { get; init; } = 1;
    public string Ip { get; init; } = "0.0.0.0";
    public ushort Port { get; init; } = 8080;
    public int Backlog { get; init; } = 65535;
    public AcceptorConfig AcceptorConfig { get; init; } = new();
    public ReactorConfig[] ReactorConfigs { get; set; } = null!;
}
