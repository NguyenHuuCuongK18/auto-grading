using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ClosedXML.Excel;
using Domain.Models;
using NUnit.Framework;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Services;

namespace SolutionGrader.Core.Test.tests;

[TestFixture]
public class DockerGradingServiceTests
{
    private DockerGradingService _service = null!;
    private IRunContext _runContext = null!;

    [SetUp]
    public void SetUp()
    {
        _runContext = new RunContext();
        _service = new DockerGradingService(networkMonitor: null, runContext: _runContext);
    }

    // Helper to invoke private/protected instance methods via reflection
    private static object? CallPrivate(object instance, string name, params object?[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Method {name} not found via reflection");
        return method!.Invoke(instance, args);
    }

    private static T CallPrivateGeneric<T>(object instance, string name, params object?[] args)
        => (T)CallPrivate(instance, name, args)!;

    private static object? CallPrivateStatic(Type type, string name, params object?[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Static method {name} not found via reflection");
        return method!.Invoke(null, args);
    }

    private static T CallPrivateStaticGeneric<T>(Type type, string name, params object?[] args)
        => (T)CallPrivateStatic(type, name, args)!;

    [Test]
    public void UT01_NormalizeFlags_WithHyphenSeparated_ReturnsSortedCommaSeparated()
    {
        // Arrange
        var flagsInput = "PSH-ACK"; // tcpdump style

        // Act
        var normalized = CallPrivateStaticGeneric<string>(typeof(DockerGradingService), "NormalizeFlags", flagsInput);

        // Assert
        Assert.That(normalized, Is.EqualTo("ACK, PSH"));
    }

    [Test]
    public void UT19_NormalizeFlags_WhitespaceAndCommas_ReturnsSorted()
    {
        var input = " ACK ,  SYN ";
        var normalized = CallPrivateStaticGeneric<string>(typeof(DockerGradingService), "NormalizeFlags", input);
        Assert.That(normalized, Is.EqualTo("ACK, SYN"));
    }

    [Test]
    public void UT20_NormalizeFlags_Empty_ReturnsEmpty()
    {
        var normalized = CallPrivateStaticGeneric<string>(typeof(DockerGradingService), "NormalizeFlags", "");
        Assert.That(normalized, Is.EqualTo(""));
    }

    #region Removed Tests for FlagsMatch
    //[Test]
    //public void UT02_FlagsMatch_DifferentOrderAndSeparators_ReturnsTrue()
    //{
    //    // Arrange
    //    var a = "ACK, PSH"; // Excel-style
    //    var b = "PSH-ACK";  // tcpdump-style

    //    // Act
    //    var match = CallPrivateStaticGeneric<bool>(typeof(DockerGradingService), "FlagsMatch", a, b);

    //    // Assert
    //    Assert.That(match, Is.True);
    //}

    //[Test]
    //public void UT08_FlagsMatch_DifferentSets_ReturnsFalse()
    //{
    //    var a = "ACK, PSH";
    //    var b = "ACK";
    //    var match = CallPrivateStaticGeneric<bool>(typeof(DockerGradingService), "FlagsMatch", a, b);
    //    Assert.That(match, Is.False);
    //}

    //[Test]
    //public void UT09_FlagsMatch_EmptyVsEmpty_ReturnsTrue()
    //{
    //    var match = CallPrivateStaticGeneric<bool>(typeof(DockerGradingService), "FlagsMatch", "", "");
    //    Assert.That(match, Is.True);
    //}

    //[Test]
    //public void UT10_FlagsMatch_EmptyVsNonEmpty_ReturnsFalse()
    //{
    //    var match = CallPrivateStaticGeneric<bool>(typeof(DockerGradingService), "FlagsMatch", "", "ACK");
    //    Assert.That(match, Is.False);
    //}
    #endregion

    [Test]
    public void UT03_NormalizeAndContains_IgnoreNewLines_ReturnsTrue()
    {
        var actual = "Hello\r\nWorld";
        var expected = "Hello\nWorld"; // different newline form

        var result = CallPrivateGeneric<bool>(_service, "NormalizeAndContains", actual, expected);
        Assert.That(result, Is.True);
    }

    [Test]
    public void UT11_NormalizeAndContains_EmptyExpected_ReturnsTrue()
    {
        var result = CallPrivateGeneric<bool>(_service, "NormalizeAndContains", "anything", "");
        Assert.That(result, Is.True);
    }

    [Test]
    public void UT12_NormalizeAndContains_EmptyActual_ReturnsFalseWhenExpectedNonEmpty()
    {
        var result = CallPrivateGeneric<bool>(_service, "NormalizeAndContains", "", "abc");
        Assert.That(result, Is.False);
    }

    [Test]
    public void UT13_NormalizeAndContains_SubstringBoundary_ReturnsTrue()
    {
        var actual = "abc";
        var expected = "abc";
        var result = CallPrivateGeneric<bool>(_service, "NormalizeAndContains", actual, expected);
        Assert.That(result, Is.True);
    }

    [Test]
    public void UT04_ModifyAppsettingsFile_UpdateIpPortAndConnectionString_WritesExpectedJson()
    {
        // Normal case: typical server appsettings with all fields present
        var tmp = Path.GetTempFileName();
        try
        {
            var initial = new
            {
                ConnectionStrings = new { MyCnn = "Server=.;Database=Db;User Id=sa;Password=1;" },
                IpAddress = "0.0.0.0",
                Port = 1234
            };
            File.WriteAllText(tmp, JsonSerializer.Serialize(initial));

            // Act
            var modified = CallPrivateGeneric<bool>(
                _service,
                "ModifyAppsettingsFile",
                tmp,
                "127.0.0.1",
                4000,
                "Server=localhost,1433;Database=Db_Stu;User Id=sa;Password=pass;",
                "Server");

            // Assert
            Assert.That(modified, Is.True);
            var text = File.ReadAllText(tmp);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            Assert.That(root.GetProperty("IpAddress").GetString(), Is.EqualTo("127.0.0.1"));

            // Port can be number or string depending on existing type; accept both
            var portProp = root.GetProperty("Port");
            var portValue = portProp.ValueKind == JsonValueKind.String
                ? int.Parse(portProp.GetString()!)
                : portProp.GetInt32();
            Assert.That(portValue, Is.EqualTo(4000));

            Assert.That(root.GetProperty("ConnectionStrings").GetProperty("MyCnn").GetString(),
                Is.EqualTo("Server=localhost,1433;Database=Db_Stu;User Id=sa;Password=pass;"));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Test]
    public void UT14_ModifyAppsettingsFile_NoConnectionStrings_StillUpdatesIpAndPort()
    {
        // Boundary case: client-style appsettings without ConnectionStrings
        var tmp = Path.GetTempFileName();
        try
        {
            var initial = new { IpAddress = "0.0.0.0", Port = "1234" }; // Port as string
            File.WriteAllText(tmp, JsonSerializer.Serialize(initial));

            var modified = CallPrivateGeneric<bool>(
                _service,
                "ModifyAppsettingsFile",
                tmp,
                "127.0.0.1",
                5000,
                null,
                "Client");

            Assert.That(modified, Is.True);
            using var json = JsonDocument.Parse(File.ReadAllText(tmp));
            var root = json.RootElement;
            Assert.That(root.GetProperty("IpAddress").GetString(), Is.EqualTo("127.0.0.1"));
            var portProp = root.GetProperty("Port");
            var portValue = portProp.ValueKind == JsonValueKind.String ? int.Parse(portProp.GetString()!) : portProp.GetInt32();
            Assert.That(portValue, Is.EqualTo(5000));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Test]
    public void UT15_ModifyAppsettingsFile_InvalidJson_ReturnsFalse()
    {
        // Abnormal case: invalid JSON content
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "{ not json }");
            var modified = CallPrivateGeneric<bool>(
                _service,
                "ModifyAppsettingsFile",
                tmp,
                "127.0.0.1",
                4000,
                null,
                "Server");
            Assert.That(modified, Is.False);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Test]
    public void UT21_ModifyAppsettingsFile_FileNotFound_ReturnsFalse()
    {
        // Abnormal case: file path does not exist
        var nonExist = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".json");
        var modified = CallPrivateGeneric<bool>(
            _service,
            "ModifyAppsettingsFile",
            nonExist,
            "127.0.0.1",
            4000,
            null,
            "Server");
        Assert.That(modified, Is.False);
    }

    [Test]
    public void UT22_ModifyAppsettingsFile_NoModifiableProperties_ReturnsFalse()
    {
        // Boundary case: JSON lacks IpAddress/Port/ConnectionStrings
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { Foo = "Bar" }));
            var modified = CallPrivateGeneric<bool>(
                _service,
                "ModifyAppsettingsFile",
                tmp,
                "127.0.0.1",
                4000,
                null,
                "Server");
            Assert.That(modified, Is.False);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}

