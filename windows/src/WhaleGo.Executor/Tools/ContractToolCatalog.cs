using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WhaleGo.Contracts.Domain;

namespace WhaleGo.Executor.Tools;

/// <summary>Loads contracts/whalego.tools.v1.json once and exposes exactly the model-facing tools/list (x-internal stripped).</summary>
public sealed class ContractToolCatalog
{
    private ContractToolCatalog(ContractDocument document, string contractPath)
    {
        Document = document;
        ContractPath = contractPath;
        Tools = BuildTools(document);
    }

    public ContractDocument Document { get; }

    public string ContractPath { get; }

    public IReadOnlyList<Tool> Tools { get; }

    public static ContractToolCatalog Load(string? explicitPath = null)
    {
        var path = ResolveContractPath(explicitPath)
            ?? throw new FileNotFoundException(
                "无法找到 contracts/whalego.tools.v1.json；请把它随可执行文件部署（contracts/ 子目录）或用 WHALEGO_CONTRACT_PATH 指定。已尝试：" +
                string.Join("；", Candidates(explicitPath)));

        ContractDocument document;
        try
        {
            document = ContractCatalog.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("工具契约文件无效：" + path + "：" + ex.Message, ex);
        }

        var forbidden = document.FindForbiddenModelParams();
        if (forbidden.Count > 0)
        {
            throw new InvalidDataException("工具契约违反了模型面最小权限（出现内部参数 " + string.Join(",", forbidden) + "）：" + path);
        }

        if (document.Tools.Count == 0)
        {
            throw new InvalidDataException("工具契约 tools[] 为空：" + path);
        }

        return new ContractToolCatalog(document, path);
    }

    public static string? ResolveContractPath(string? explicitPath)
    {
        foreach (var candidate in Candidates(explicitPath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
        }

        var environment = Environment.GetEnvironmentVariable("WHALEGO_CONTRACT_PATH");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            yield return environment;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "contracts", "whalego.tools.v1.json");

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            yield return Path.Combine(directory.FullName, "contracts", "whalego.tools.v1.json");
            directory = directory.Parent;
        }
    }

    private static IReadOnlyList<Tool> BuildTools(ContractDocument document)
    {
        var tools = new List<Tool>(document.Tools.Count);
        foreach (var node in document.ModelTools())
        {
            if (node is not JsonObject toolObject)
            {
                continue;
            }

            var name = toolObject["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = toolObject["description"]?.GetValue<string>();
            var schemaJson = toolObject["inputSchema"]?.ToJsonString() ?? "{\"type\":\"object\",\"properties\":{}}";
            using var schemaDocument = JsonDocument.Parse(schemaJson);
            tools.Add(new Tool
            {
                Name = name,
                Description = description,
                InputSchema = schemaDocument.RootElement.Clone(),
            });
        }
        return tools;
    }
}
