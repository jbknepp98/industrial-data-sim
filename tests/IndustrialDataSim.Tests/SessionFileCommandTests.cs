using System.Text;
using System.Text.Json;
using IndustrialDataSim.Cli;

namespace IndustrialDataSim.Tests;

public class SessionFileCommandTests
{
    [Fact]
    public void ValidFileReturnsSuccess()
    {
        CheckFile(Encoding.UTF8.GetBytes(SessionDefinitionLoaderTests.ValidJson), 0, null);
    }

    [Fact]
    public void Utf8BomIsAccepted()
    {
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(SessionDefinitionLoaderTests.ValidJson)];
        CheckFile(bytes, 0, null);
    }

    [Fact]
    public void MalformedJsonReturnsValidationFailure()
    {
        CheckFile(Encoding.UTF8.GetBytes("{private-invalid-data"), 1, "session.invalid_json");
    }

    [Fact]
    public void MalformedUtf8IsNotSilentlyReplaced()
    {
        CheckFile([0xff, 0xff], 1, "session.invalid_encoding");
    }

    [Fact]
    public void UnpairedUnicodeEscapeReturnsJsonInsteadOfThrowing()
    {
        CheckFile(Encoding.UTF8.GetBytes("{\"sessionId\":\"\\ud800\"}"), 1, "session.invalid_json");
    }

    [Fact]
    public void OversizedFileIsRejectedBeforeParsing()
    {
        CheckFile(new byte[1024 * 1024 + 1], 1, "session.file_too_large");
    }

    [Fact]
    public void MissingFileReturnsInputFailureWithoutExposingPath()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "private-session.json");
        using var output = new StringWriter();
        int exitCode = CliApplication.Run(["validate-session", path], output);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(3, exitCode);
        Assert.False(result.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal("session.file_unreadable", result.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Contains("existing file", output.ToString());
        Assert.Contains("read permission", output.ToString());
        Assert.DoesNotContain("private-session", output.ToString());
        Assert.DoesNotContain(path, output.ToString());
    }

    private static void CheckFile(byte[] content, int expectedExitCode, string? expectedError)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var output = new StringWriter();
            int exitCode = CliApplication.Run(["validate-session", path], output);
            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal(expectedExitCode, exitCode);
            Assert.Equal(expectedExitCode == 0, result.RootElement.GetProperty("valid").GetBoolean());
            if (expectedError is not null)
            {
                Assert.Equal(expectedError, result.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
            }
            Assert.DoesNotContain(path, output.ToString());
            Assert.DoesNotContain("private-invalid-data", output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
