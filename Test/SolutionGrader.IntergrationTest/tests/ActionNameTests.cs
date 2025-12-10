using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolutionGrader.IntergrationTest.tests
{
    /// <summary>
    /// Naming rule of thumb: [TestID]_[Action]_[StateUnderTest]_[ExpectedBehavior]
    /// eg: TC01_NetworkMonitor_DeviceOpenStartStop_SucceedsAndRaisesEvents
    /// </summary>

    internal class ActionNameTests
    {
        // Setup once before all test cases
        [OneTimeSetUp]
        public void OneTimeSetup()
        {
        }

        // Setup between each test case
        [SetUp]
        public void Setup()
        {
        }

        // Dispose/reset resources between each test case
        [TearDown]
        public void TearDown()
        {
        }

        // Dispose/reset resources once after all test cases
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
        }
    }
}
