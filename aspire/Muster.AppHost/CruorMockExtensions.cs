namespace Muster.AppHost;

/// <summary>
/// AppHost composition for the <b>Cruor mock</b> — a local-only test double for the external Cruor
/// loot/economy platform the currency connector pushes to (see <c>tools/cruor-mock</c>). Like the other
/// emulators (Service Bus, SQL container), it's a dev convenience: built from its Dockerfile and run as a
/// container <b>only in run mode</b>, and a complete no-op in publish so it never reaches production.
/// </summary>
internal static class CruorMockExtensions
{
    /// <summary>Aspire resource id for the Cruor mock container.</summary>
    private const string ResourceName = "cruor-mock";

    /// <summary>Docker build context — the repo root (relative to the AppHost project dir), so the central
    /// package manifests (<c>Directory.Build.props</c> / <c>Directory.Packages.props</c>) are in scope for the
    /// project's restore.</summary>
    private const string ContextPath = "../..";

    /// <summary>Path to the mock's Dockerfile, relative to <see cref="ContextPath"/>.</summary>
    private const string DockerfilePath = "tools/Muster.CruorMock/Dockerfile";

    /// <summary>Host/proxy port the mock listens on — kept fixed so a currency connector can target a stable
    /// <c>http://localhost:8081/currency/add-cruor</c> across runs.</summary>
    private const int HostPort = 8081;

    /// <summary>Container's internal listen port (matches the Dockerfile's uvicorn port).</summary>
    private const int TargetPort = 8080;

    /// <summary>API key the mock enforces on the <c>x-api-key</c> header; point the connector's secret at this.</summary>
    private const string ApiKey = "test-key";

    /// <summary>Named docker volume holding the mock's SQLite file, so data survives container restarts.</summary>
    private const string DataVolumeName = "cruor-mock-data";

    /// <summary>
    /// Run-mode only. Builds <c>tools/cruor-mock</c> and runs it as a container so the local stack can
    /// exercise the external-currency connector end-to-end. No-op in publish (returns <c>null</c>), so it's
    /// safe to call unconditionally from <c>AppHost.cs</c> and is never part of the deployed topology.
    /// </summary>
    /// <returns>The container resource in run mode; <c>null</c> in publish.</returns>
    public static IResourceBuilder<ContainerResource>? AddMusterCruorMock(this IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsPublishMode)
        {
            return null;
        }

        return builder.AddDockerfile(ResourceName, ContextPath, DockerfilePath)
            .WithHttpEndpoint(port: HostPort, targetPort: TargetPort, name: "http")
            .WithEnvironment("API_KEY", ApiKey)
            .WithVolume(DataVolumeName, "/data")  // persist the SQLite DB across container restarts
            .WithExternalHttpEndpoints()
            .WithUrlForEndpoint("http", url => url.Url = "/ui"); // dashboard link opens the test UI
    }
}
