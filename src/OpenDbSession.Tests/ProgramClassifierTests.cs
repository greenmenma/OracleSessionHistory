using OpenDbSession.Core.Entities;
using OpenDbSession.Core.Tracking;

namespace OpenDbSession.Tests;

public class ProgramClassifierTests
{
    [Theory]
    [InlineData("販売管理.exe", "USER", ProcessKinds.Client)]
    [InlineData("w3wp.exe", "USER", ProcessKinds.Client)]
    [InlineData("sqlplus@apsv (TNS V1-V3)", "USER", ProcessKinds.Client)]
    [InlineData("ORACLE.EXE", "USER", ProcessKinds.DbLinkSuspect)]
    [InlineData("oracle.exe", "USER", ProcessKinds.DbLinkSuspect)]
    [InlineData("oracle@kikan-db (TNS V1-V3)", "USER", ProcessKinds.DbLinkSuspect)]
    [InlineData("oracle@kikan-db", "USER", ProcessKinds.DbLinkSuspect)]
    [InlineData("ORACLE.EXE (SHAD)", "USER", ProcessKinds.DbLinkSuspect)]
    [InlineData("ORACLE.EXE (J000)", "USER", ProcessKinds.JobSlave)]
    [InlineData("oracle@koukai-db (J012)", "USER", ProcessKinds.JobSlave)]
    [InlineData("ORACLE.EXE (P001)", "USER", ProcessKinds.ParallelSlave)]
    [InlineData("oracle@db (PZ99)", "USER", ProcessKinds.ParallelSlave)]
    [InlineData("ORACLE.EXE (Q003)", "USER", ProcessKinds.AqSlave)]
    [InlineData("ORACLE.EXE (DW00)", "USER", ProcessKinds.DataPump)]
    [InlineData("ORACLE.EXE (M001)", "USER", ProcessKinds.OracleInternal)]
    [InlineData("ORACLE.EXE (PMON)", "BACKGROUND", ProcessKinds.Background)]
    [InlineData(null, "USER", ProcessKinds.Client)]
    public void Classify(string? program, string type, string expected) =>
        Assert.Equal(expected, ProgramClassifier.Classify(program, type));

    [Theory]
    [InlineData("1234:5678", new[] { "1234", "5678" })]
    [InlineData("4321", new[] { "4321" })]
    [InlineData("abc", new string[0])]
    [InlineData(null, new string[0])]
    public void ProcessIdCandidates(string? process, string[] expected) =>
        Assert.Equal(expected, ProgramClassifier.ProcessIdCandidates(process).ToArray());
}
