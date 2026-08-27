namespace DocuEngAIne.Core.Interfaces;

public interface IMcpClient
{
    Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default);
    Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default);
}
