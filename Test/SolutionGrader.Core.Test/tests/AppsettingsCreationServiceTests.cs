using System;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Services;

namespace SolutionGrader.Core.Test.tests;

[TestFixture]
public class AppsettingsCreationServiceTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ag_appsettings_ut_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private static string CreateDummyExe(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, fileName);
        File.WriteAllText(exe, "dummy");
        return exe;
    }

    [Test]
    public void UT01_GenerateAppsettings_Normal_BothPathsCreateFiles_WithPortsFromConfig()
    {
        var gradingConfig = new GradingConfig { GraderPort = 4321 };
        var service = new AppsettingsCreationService(gradingConfig);

        var serverExe = CreateDummyExe(Path.Combine(_tempRoot, "server"), "server.exe");
        var clientExe = CreateDummyExe(Path.Combine(_tempRoot, "client"), "client.exe");

        var (clientPort, serverPort) = service.GenerateAppsettings(
            dbConfig: new DatabaseConfiguration
            {
                Type = "HTTP",
                SqlServer = ".",
                Database = "Db",
                Username = "sa",
                Password = "pwd"
            },
            clientExePath: clientExe,
            serverExePath: serverExe
        );

        Assert.That(clientPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(serverPort, Is.EqualTo(gradingConfig.GraderPort));

        var clientAppsettingsPath = Path.Combine(Path.GetDirectoryName(clientExe)!, "appsettings.json");
        var serverAppsettingsPath = Path.Combine(Path.GetDirectoryName(serverExe)!, "appsettings.json");
        Assert.That(File.Exists(clientAppsettingsPath), Is.True);
        Assert.That(File.Exists(serverAppsettingsPath), Is.True);

        using (var clientJson = JsonDocument.Parse(File.ReadAllText(clientAppsettingsPath)))
        {
            var root = clientJson.RootElement;
            Assert.That(root.GetProperty("Port").GetString(), Is.EqualTo(gradingConfig.GraderPort.ToString()));
            Assert.That(root.TryGetProperty("IpAddress", out _), Is.True);
            Assert.That(root.TryGetProperty("ConnectionStrings", out _), Is.False);
        }

        using (var serverJson = JsonDocument.Parse(File.ReadAllText(serverAppsettingsPath)))
        {
            var root = serverJson.RootElement;
            Assert.That(root.GetProperty("Port").GetString(), Is.EqualTo(gradingConfig.GraderPort.ToString()));
            Assert.That(root.TryGetProperty("IpAddress", out _), Is.True);
            var cnn = root.GetProperty("ConnectionStrings").GetProperty("MyCnn").GetString();
            Assert.That(string.IsNullOrEmpty(cnn), Is.False);
        }
    }

    [Test]
    public void UT02_GenerateAppsettings_Abnormal_WithoutPaths_ReturnsPortsNoFiles()
    {
        var gradingConfig = new GradingConfig { GraderPort = 5678 };
        var service = new AppsettingsCreationService(gradingConfig);

        var (clientPort, serverPort) = service.GenerateAppsettings(
            dbConfig: null,
            clientExePath: null,
            serverExePath: null
        );

        Assert.That(clientPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(serverPort, Is.EqualTo(gradingConfig.GraderPort));
    }

    [Test]
    public void UT03_GenerateAppsettings_Boundary_ProtocolTcpVsHttp_IpAddressDiffers()
    {
        var gradingConfig = new GradingConfig { GraderPort = 6000 };
        var service = new AppsettingsCreationService(gradingConfig);

        var serverExe = CreateDummyExe(Path.Combine(_tempRoot, "server_tcp"), "server.exe");
        var clientExe = CreateDummyExe(Path.Combine(_tempRoot, "client_tcp"), "client.exe");

        // TCP
        service.GenerateAppsettings(
            dbConfig: null,
            clientExePath: clientExe,
            serverExePath: serverExe,
            envConfig: null,
            protocol: "TCP"
        );

        var clientAppsettingsPathTcp = Path.Combine(Path.GetDirectoryName(clientExe)!, "appsettings.json");
        Assert.That(File.Exists(clientAppsettingsPathTcp), Is.True);

        // HTTP
        var serverExeHttp = CreateDummyExe(Path.Combine(_tempRoot, "server_http"), "server.exe");
        var clientExeHttp = CreateDummyExe(Path.Combine(_tempRoot, "client_http"), "client.exe");
        service.GenerateAppsettings(
            dbConfig: null,
            clientExePath: clientExeHttp,
            serverExePath: serverExeHttp,
            envConfig: null,
            protocol: "HTTP"
        );

        var clientAppsettingsPathHttp = Path.Combine(Path.GetDirectoryName(clientExeHttp)!, "appsettings.json");
        Assert.That(File.Exists(clientAppsettingsPathHttp), Is.True);

        using var clientTcpJson = JsonDocument.Parse(File.ReadAllText(clientAppsettingsPathTcp));
        using var clientHttpJson = JsonDocument.Parse(File.ReadAllText(clientAppsettingsPathHttp));
        var ipTcp = clientTcpJson.RootElement.GetProperty("IpAddress").GetString();
        var ipHttp = clientHttpJson.RootElement.GetProperty("IpAddress").GetString();
        Assert.That(ipTcp, Is.Not.Null);
        Assert.That(ipHttp, Is.Not.Null);
        Assert.That(ipTcp, Is.Not.EqualTo(ipHttp));
    }

    [Test]
    public void UT04_GetPorts_Normal_AfterGenerate_ReturnsSameClientAndServerPort()
    {
        var gradingConfig = new GradingConfig { GraderPort = 7001 };
        var service = new AppsettingsCreationService(gradingConfig);

        var serverExe = CreateDummyExe(Path.Combine(_tempRoot, "server_getports"), "server.exe");
        var clientExe = CreateDummyExe(Path.Combine(_tempRoot, "client_getports"), "client.exe");

        var (clientPort, serverPort) = service.GenerateAppsettings(
            dbConfig: null,
            clientExePath: clientExe,
            serverExePath: serverExe
        );

        var (gpClientPort, gpServerPort) = service.GetPorts();

        Assert.That(clientPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(serverPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(gpClientPort, Is.EqualTo(clientPort));
        Assert.That(gpServerPort, Is.EqualTo(serverPort));
    }

    [Test]
    public void UT05_GenerateAppsettings_Boundary_OnlyClientPath_CreatesClientAppsettingsOnly()
    {
        var gradingConfig = new GradingConfig { GraderPort = 7100 };
        var service = new AppsettingsCreationService(gradingConfig);
        var clientExe = CreateDummyExe(Path.Combine(_tempRoot, "client_only"), "client.exe");

        var (clientPort, serverPort) = service.GenerateAppsettings(
            dbConfig: null,
            clientExePath: clientExe,
            serverExePath: null
        );

        Assert.That(clientPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(serverPort, Is.EqualTo(gradingConfig.GraderPort));
        var clientAppsettingsPath = Path.Combine(Path.GetDirectoryName(clientExe)!, "appsettings.json");
        Assert.That(File.Exists(clientAppsettingsPath), Is.True);
    }

    [Test]
    public void UT06_GenerateAppsettings_Boundary_OnlyServerPath_CreatesServerAppsettingsOnly()
    {
        var gradingConfig = new GradingConfig { GraderPort = 7200 };
        var service = new AppsettingsCreationService(gradingConfig);
        var serverExe = CreateDummyExe(Path.Combine(_tempRoot, "server_only"), "server.exe");

        var (clientPort, serverPort) = service.GenerateAppsettings(
            dbConfig: new DatabaseConfiguration { Type = "HTTP", SqlServer = ".", Database = "X", Username = "sa", Password = "p" },
            clientExePath: null,
            serverExePath: serverExe
        );

        Assert.That(clientPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(serverPort, Is.EqualTo(gradingConfig.GraderPort));
        var serverAppsettingsPath = Path.Combine(Path.GetDirectoryName(serverExe)!, "appsettings.json");
        Assert.That(File.Exists(serverAppsettingsPath), Is.True);
        using var serverJson = JsonDocument.Parse(File.ReadAllText(serverAppsettingsPath));
        var cnn = serverJson.RootElement.GetProperty("ConnectionStrings").GetProperty("MyCnn").GetString();
        Assert.That(string.IsNullOrEmpty(cnn), Is.False);
    }

    [Test]
    public void UT07_GenerateAppsettings_Normal_ProtocolFromDbConfigType()
    {
        var gradingConfig = new GradingConfig { GraderPort = 7300 };
        var service = new AppsettingsCreationService(gradingConfig);
        var serverExe = CreateDummyExe(Path.Combine(_tempRoot, "server_dbtype"), "server.exe");
        var clientExe = CreateDummyExe(Path.Combine(_tempRoot, "client_dbtype"), "client.exe");

        service.GenerateAppsettings(
            dbConfig: new DatabaseConfiguration { Type = "TCP" },
            clientExePath: clientExe,
            serverExePath: serverExe
        );

        var clientAppsettingsPath = Path.Combine(Path.GetDirectoryName(clientExe)!, "appsettings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(clientAppsettingsPath));
        var ip = json.RootElement.GetProperty("IpAddress").GetString();
        Assert.That(ip, Is.Not.Null);
    }

    [Test]
    public void UT08_GenerateAppsettings_Normal_OverloadWithEnvConfig()
    {
        var gradingConfig = new GradingConfig { GraderPort = 7400 };
        var service = new AppsettingsCreationService(gradingConfig);
        var serverExe = CreateDummyExe(Path.Combine(_tempRoot, "server_env"), "server.exe");
        var clientExe = CreateDummyExe(Path.Combine(_tempRoot, "client_env"), "client.exe");

        var (cPort, sPort) = service.GenerateAppsettings(
            dbConfig: new DatabaseConfiguration { Type = "HTTP" },
            clientExePath: clientExe,
            serverExePath: serverExe,
            envConfig: new EnvironmentConfiguration { },
            protocol: null
        );

        Assert.That(cPort, Is.EqualTo(gradingConfig.GraderPort));
        Assert.That(sPort, Is.EqualTo(gradingConfig.GraderPort));
    }
}

// Test case classification:
// Normal: UT01, UT04, UT07, UT08
// Abnormal: UT02
// Boundary: UT03, UT05, UT06

