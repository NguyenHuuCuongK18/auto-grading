using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ClosedXML.Excel;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Domain.Models; // Step
using SolutionGrader.Core.Keywords; // Keywords

namespace SolutionGrader.UI.Test.tests.services
{
    [TestFixture]
    public class NewFormatDetailParserTests
    {
        private const string QuestionCode = "Q001";

        private static XLWorkbook CreateWorkbook(
            Action<IXLWorksheet>? userBuilder = null,
            Action<IXLWorksheet>? clientBuilder = null,
            Action<IXLWorksheet>? serverBuilder = null,
            Action<IXLWorksheet>? networkBuilder = null)
        {
            var wb = new XLWorkbook();

            var user = wb.Worksheets.Add(SuiteKeywords.Sheet_User);
            user.Cell(1, 1).Value = "Stage";
            user.Cell(1, 2).Value = "Action";
            user.Cell(1, 3).Value = "Input";
            userBuilder?.Invoke(user);

            var client = wb.Worksheets.Add(SuiteKeywords.Sheet_Client);
            client.Cell(1, 1).Value = "Stage";
            client.Cell(1, 2).Value = "Console";
            clientBuilder?.Invoke(client);

            var server = wb.Worksheets.Add(SuiteKeywords.Sheet_Server);
            server.Cell(1, 1).Value = "Stage";
            server.Cell(1, 2).Value = "Console";
            serverBuilder?.Invoke(server);

            var network = wb.Worksheets.Add(SuiteKeywords.Sheet_Network);
            network.Cell(1, 1).Value = NetworkKeywords.Col_Stage;
            network.Cell(1, 2).Value = NetworkKeywords.Col_Info;
            network.Cell(1, 3).Value = NetworkKeywords.Col_Flags;
            network.Cell(1, 4).Value = NetworkKeywords.Col_State;
            network.Cell(1, 5).Value = NetworkKeywords.Col_SourceRole;
            network.Cell(1, 6).Value = NetworkKeywords.Col_DestinationRole;
            network.Cell(1, 7).Value = NetworkKeywords.Col_Data;
            network.Cell(1, 8).Value = NetworkKeywords.Col_URI;
            network.Cell(1, 9).Value = NetworkKeywords.Col_Method;
            network.Cell(1, 10).Value = NetworkKeywords.Col_Status;
            network.Cell(1, 11).Value = NetworkKeywords.Col_HttpBody;
            networkBuilder?.Invoke(network);

            return wb;
        }

        [Test]
        public void UT01_ParseDetail_UserStartClientThenServer_ProxyInjectedAndWaitAdded()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1";
                    ws.Cell(2, 2).Value = "StartClient";
                    ws.Cell(3, 1).Value = "1";
                    ws.Cell(3, 2).Value = "StartServer";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);

            Assert.That(steps.Any(s => s.Action == ActionKeywords.ClientStart));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.ServerStart));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.TcpRelay));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.Wait && s.Id.StartsWith("USER-MIDDLEWAIT")));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.Wait && s.Id.StartsWith("USER-WAIT")));
        }

        [Test]
        public void UT02_ParseDetail_UserStartServerThenClient_ProxyInjectedAndWaitAdded()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1";
                    ws.Cell(2, 2).Value = "StartServer";
                    ws.Cell(3, 1).Value = "1";
                    ws.Cell(3, 2).Value = "StartClient";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Count(s => s.Action == ActionKeywords.TcpRelay), Is.EqualTo(1));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.Wait && s.Id.StartsWith("USER-MIDDLEWAIT")));
        }

        [Test]
        public void UT03_ParseDetail_UserInput_ProducesClientInputAndWait()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1";
                    ws.Cell(2, 2).Value = "Input";
                    ws.Cell(2, 3).Value = "hello";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            var inputStep = steps.FirstOrDefault(s => s.Action == ActionKeywords.ClientInput);
            Assert.NotNull(inputStep);
            Assert.That(inputStep!.Value, Is.EqualTo("hello"));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.Wait));
        }

        [Test]
        public void UT04_ParseDetail_EmptyInput_ProducesClientInputWithEmptyValue()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1";
                    ws.Cell(2, 2).Value = "Input";
                    ws.Cell(2, 3).Value = string.Empty;
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            var inputStep = steps.FirstOrDefault(s => s.Action == ActionKeywords.ClientInput);
            Assert.NotNull(inputStep);
            Assert.That(inputStep!.Value, Is.EqualTo(string.Empty));
        }

        [Test]
        public void UT05_ParseDetail_CloseClientAndServer_ResetNetworkInitialization()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "StartClient";
                    ws.Cell(3, 1).Value = "1"; ws.Cell(3, 2).Value = "StartServer";
                    ws.Cell(4, 1).Value = "2"; ws.Cell(4, 2).Value = "CloseClient";
                    ws.Cell(5, 1).Value = "3"; ws.Cell(5, 2).Value = "CloseServer";
                    ws.Cell(6, 1).Value = "4"; ws.Cell(6, 2).Value = "StartClient";
                    ws.Cell(7, 1).Value = "4"; ws.Cell(7, 2).Value = "StartServer";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Count(s => s.Action == ActionKeywords.TcpRelay), Is.EqualTo(2));
        }

        [Test]
        public void UT06_ParseDetail_ClientOutput_CompareTextStepCreated()
        {
            var wb = CreateWorkbook(
                clientBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1";
                    ws.Cell(2, 2).Value = "Client says hi";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            var compare = steps.FirstOrDefault(s => s.Metadata?.ContainsKey(GradingKeywords.MetadataKey_ValidationType) == true &&
                                                    Equals(s.Metadata![GradingKeywords.MetadataKey_ValidationType], GradingKeywords.Validation_ClientOutput));
            Assert.NotNull(compare);
            Assert.That(compare!.Target, Is.EqualTo("Client says hi"));
        }

        [Test]
        public void UT07_ParseDetail_ServerOutput_CompareTextStepCreated()
        {
            var wb = CreateWorkbook(
                serverBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1";
                    ws.Cell(2, 2).Value = "Server ready";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            var compare = steps.FirstOrDefault(s => s.Metadata?.ContainsKey(GradingKeywords.MetadataKey_ValidationType) == true &&
                                                    Equals(s.Metadata![GradingKeywords.MetadataKey_ValidationType], GradingKeywords.Validation_ServerOutput));
            Assert.NotNull(compare);
            Assert.That(compare!.Target, Is.EqualTo("Server ready"));
        }

        [Test]
        public void UT08_ParseDetail_NetworkFlow_AllRowsProduceValidation()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    // Two rows for stage 1
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "SYN"; ws.Cell(2, 4).Value = "SYN_SENT"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server;
                    ws.Cell(3, 1).Value = "1"; ws.Cell(3, 2).Value = "TCP"; ws.Cell(3, 3).Value = "SYN-ACK"; ws.Cell(3, 4).Value = "SYN_RECEIVED"; ws.Cell(3, 5).Value = NetworkKeywords.Role_Server; ws.Cell(3, 6).Value = NetworkKeywords.Role_Client;
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Count(s => s.Action == ActionKeywords.CompareNetworkFlow), Is.EqualTo(2));
            Assert.That(steps.Where(s => s.Action == ActionKeywords.CompareNetworkFlow).Select(s => s.NetworkRowIndex), Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void UT09_ParseDetail_NetworkPerStageIndexing_IndexesResetPerStage()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "ACK"; ws.Cell(2, 4).Value = "ESTABLISHED"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server;
                    ws.Cell(3, 1).Value = "2"; ws.Cell(3, 2).Value = "TCP"; ws.Cell(3, 3).Value = "FIN"; ws.Cell(3, 4).Value = "FIN_WAIT"; ws.Cell(3, 5).Value = NetworkKeywords.Role_Server; ws.Cell(3, 6).Value = NetworkKeywords.Role_Client;
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            var flowSteps = steps.Where(s => s.Action == ActionKeywords.CompareNetworkFlow).ToList();
            Assert.That(flowSteps.Any(s => s.Stage == "1" && s.NetworkRowIndex == 1));
            Assert.That(flowSteps.Any(s => s.Stage == "2" && s.NetworkRowIndex == 1));
        }

        [Test]
        public void UT10_ParseDetail_HttpRequest_ValidatesMethodAndBody()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "HTTP"; ws.Cell(2, 3).Value = "PSH-ACK"; ws.Cell(2, 4).Value = "REQUEST"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server;
                    ws.Cell(2, 9).Value = "POST"; ws.Cell(2, 11).Value = "{\"a\":1}";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_HttpMethod) == true));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.CompareJson && s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_DataRequest) == true));
        }

        [Test]
        public void UT11_ParseDetail_HttpResponse_ValidatesStatusAndBody()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "HTTP"; ws.Cell(2, 3).Value = "PSH-ACK"; ws.Cell(2, 4).Value = "RESPONSE"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Server; ws.Cell(2, 6).Value = NetworkKeywords.Role_Client;
                    ws.Cell(2, 10).Value = "200"; ws.Cell(2, 11).Value = "<xml>ok</xml>";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_StatusCode) == true));
            Assert.That(steps.Any(s => s.Action == ActionKeywords.CompareText && s.DataType == "TEXT" && s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_DataResponse) == true));
        }

        [Test]
        public void UT12_ParseDetail_TcpPshData_TextComparisonCreated()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "PSH-ACK"; ws.Cell(2, 4).Value = "DATA"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server; ws.Cell(2, 7).Value = "payload";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Action == ActionKeywords.CompareText && s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_DataRequest) == true));
        }

        [Test]
        public void UT13_ParseDetail_TcpPshJson_JsonComparisonCreated()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "PSH"; ws.Cell(2, 4).Value = "DATA"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Server; ws.Cell(2, 6).Value = NetworkKeywords.Role_Client; ws.Cell(2, 7).Value = "{\"x\":2}";
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Action == ActionKeywords.CompareJson && s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_DataResponse) == true));
        }

        [Test]
        public void UT14_ParseDetail_TcpFlowValidation_MetadataContainsExpected()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "ACK"; ws.Cell(2, 4).Value = "ESTABLISHED"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server;
                });

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            var flow = steps.First(s => s.Action == ActionKeywords.CompareNetworkFlow);
            Assert.That(flow.Metadata!["ExpectedFlags"], Is.EqualTo("ACK"));
            Assert.That(flow.Metadata!["ExpectedState"], Is.EqualTo("ESTABLISHED"));
            Assert.That(flow.Metadata!["ExpectedSourceRole"], Is.EqualTo(NetworkKeywords.Role_Client));
            Assert.That(flow.Metadata!["ExpectedDestRole"], Is.EqualTo(NetworkKeywords.Role_Server));
        }

        [Test]
        public void UT15_ParseDetail_MissingUserSheet_NoUserStepsProduced()
        {
            var wb = new XLWorkbook();
            wb.Worksheets.Add(SuiteKeywords.Sheet_Client).Cell(1, 1).Value = "Stage";
            wb.Worksheets.Add(SuiteKeywords.Sheet_Server).Cell(1, 1).Value = "Stage";
            wb.Worksheets.Add(SuiteKeywords.Sheet_Network).Cell(1, 1).Value = NetworkKeywords.Col_Stage;

            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.All(s => s.Action != ActionKeywords.ClientStart && s.Action != ActionKeywords.ServerStart));
        }

        [Test]
        public void UT16_ParseDetail_EmptySheets_ResultEmpty()
        {
            var wb = CreateWorkbook();
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps, Is.Empty);
        }

        [Test]
        public void UT17_ParseDetail_NetworkRowWithoutStage_Ignored()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "ACK";
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.All(s => s.Action != ActionKeywords.CompareNetworkFlow));
        }

        [Test]
        public void UT18_ParseDetail_HttpRowWithoutMethod_StatusOrBodyOnlyHandled()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "HTTP"; ws.Cell(2, 3).Value = "PSH"; ws.Cell(2, 4).Value = "REQUEST"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server; ws.Cell(2, 11).Value = "hello";
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_DataRequest) == true));
            Assert.That(steps.All(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_HttpMethod) != true));
        }

        [Test]
        public void UT19_ParseDetail_HttpResponseWithoutStatus_BodyValidatedOnly()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "HTTP"; ws.Cell(2, 3).Value = "PSH"; ws.Cell(2, 4).Value = "RESPONSE"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Server; ws.Cell(2, 6).Value = NetworkKeywords.Role_Client; ws.Cell(2, 11).Value = "{\"k\":\"v\"}";
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Action == ActionKeywords.CompareJson && s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_DataResponse) == true));
            Assert.That(steps.All(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_StatusCode) != true));
        }

        [Test]
        public void UT20_ParseDetail_MultipleStagesAndRows_CorrectStepIdsAndCounts()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "StartClient";
                    ws.Cell(3, 1).Value = "1"; ws.Cell(3, 2).Value = "StartServer";
                    ws.Cell(4, 1).Value = "1"; ws.Cell(4, 2).Value = "Input"; ws.Cell(4, 3).Value = "ABC";
                    ws.Cell(5, 1).Value = "2"; ws.Cell(5, 2).Value = "Input"; ws.Cell(5, 3).Value = "DEF";
                },
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "TCP"; ws.Cell(2, 3).Value = "ACK"; ws.Cell(2, 4).Value = "ESTABLISHED"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server;
                    ws.Cell(3, 1).Value = "2"; ws.Cell(3, 2).Value = "TCP"; ws.Cell(3, 3).Value = "PSH"; ws.Cell(3, 4).Value = "DATA"; ws.Cell(3, 5).Value = NetworkKeywords.Role_Server; ws.Cell(3, 6).Value = NetworkKeywords.Role_Client; ws.Cell(3, 7).Value = "XYZ";
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);

            Assert.That(steps.Count(s => s.Stage == "1"), Is.GreaterThan(0));
            Assert.That(steps.Count(s => s.Stage == "2"), Is.GreaterThan(0));
            Assert.That(steps.Any(s => s.Id.StartsWith("NETWORK-FLOW-1-1")));
            Assert.That(steps.Any(s => s.Id.StartsWith("NETWORK-RESPAYLOAD-2-1")));
        }

        [Test]
        public void UT21_ParseDetail_UserRowsWithoutStage_Ignored()
        {
            var wb = CreateWorkbook(
                userBuilder: ws =>
                {
                    ws.Cell(2, 2).Value = "StartClient";
                    ws.Cell(3, 1).Value = "1"; ws.Cell(3, 2).Value = "Input"; ws.Cell(3, 3).Value = "ok";
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Action == ActionKeywords.ClientInput));
            Assert.That(steps.All(s => s.Action != ActionKeywords.ClientStart));
        }

        [Test]
        public void UT22_ParseDetail_ClientServerOutputsMissingConsole_Ignored()
        {
            var wb = CreateWorkbook(
                clientBuilder: ws => { ws.Cell(2, 1).Value = "1"; },
                serverBuilder: ws => { ws.Cell(2, 1).Value = "1"; });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.All(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_ClientOutput) != true));
            Assert.That(steps.All(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_ServerOutput) != true));
        }

        [Test]
        public void UT23_ParseDetail_HttpInfoCaseInsensitive_ValidationsStillCreated()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "http"; ws.Cell(2, 3).Value = "PSH"; ws.Cell(2, 4).Value = "REQUEST"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server; ws.Cell(2, 9).Value = "GET";
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Metadata?.GetValueOrDefault(GradingKeywords.MetadataKey_ValidationType)?.Equals(GradingKeywords.Validation_HttpMethod) == true));
        }

        [Test]
        public void UT24_ParseDetail_FlagsCaseInsensitive_CompareNetworkFlowCreated()
        {
            var wb = CreateWorkbook(
                networkBuilder: ws =>
                {
                    ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "tcp"; ws.Cell(2, 3).Value = "ack"; ws.Cell(2, 4).Value = "ESTABLISHED"; ws.Cell(2, 5).Value = NetworkKeywords.Role_Client; ws.Cell(2, 6).Value = NetworkKeywords.Role_Server;
                });
            var steps = NewFormatDetailParser.ParseDetail(wb, QuestionCode);
            Assert.That(steps.Any(s => s.Action == ActionKeywords.CompareNetworkFlow));
        }
    }
}
