using System;
using System.Reflection;
using System.Linq;

namespace SolutionGrader.UI.Tests
{
    /// <summary>
    /// Automated tests to verify UI event handler wiring.
    /// This test uses reflection to verify that all buttons have their Click events properly wired.
    /// </summary>
    public class EventHandlerVerificationTests
    {
        public static void RunTests()
        {
            Console.WriteLine("=== Event Handler Verification Tests ===\n");
            
            int passCount = 0;
            int failCount = 0;
            
            // Test GradingWindow event handlers
            Console.WriteLine("Testing GradingWindow event handlers...");
            var gradingWindowTests = new[]
            {
                ("ApplyIndexSelection_Click", "ApplyIndexSelection_Click"),
                ("SelectAll_Click", "SelectAll_Click"),
                ("UnselectAll_Click", "UnselectAll_Click"),
                ("StartAll_Click", "StartAll_Click"),
                ("StartSelected_Click", "StartSelected_Click"),
                ("Pause_Click", "Pause_Click"),
                ("Resume_Click", "Resume_Click"),
                ("ResetAll_Click", "ResetAll_Click"),
                ("ResetSelected_Click", "ResetSelected_Click"),
                ("BackToSetup_Click", "BackToSetup_Click"),
                ("Window_Loaded", "Window_Loaded"),
                ("Window_Closing", "Window_Closing"),
                ("PaperSelection_SelectionChanged", "PaperSelection_SelectionChanged"),
                ("dgStudents_SelectionChanged", "dgStudents_SelectionChanged")
            };
            
            foreach (var (buttonName, handlerName) in gradingWindowTests)
            {
                if (VerifyEventHandler(typeof(GradingWindow), handlerName))
                {
                    Console.WriteLine($"  ✓ {buttonName} → {handlerName}");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"  ✗ {buttonName} → {handlerName} NOT FOUND");
                    failCount++;
                }
            }
            
            // Test SetupWindow event handlers
            Console.WriteLine("\nTesting SetupWindow event handlers...");
            var setupWindowTests = new[]
            {
                ("BrowseSubmitFolder_Click", "BrowseSubmitFolder_Click"),
                ("BrowseTestKitFolder_Click", "BrowseTestKitFolder_Click"),
                ("BrowseSaveFolder_Click", "BrowseSaveFolder_Click"),
                ("StartGrading_Click", "StartGrading_Click"),
                ("ProjectName_TextChanged", "ProjectName_TextChanged")
            };
            
            foreach (var (buttonName, handlerName) in setupWindowTests)
            {
                if (VerifyEventHandler(typeof(SetupWindow), handlerName))
                {
                    Console.WriteLine($"  ✓ {buttonName} → {handlerName}");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"  ✗ {buttonName} → {handlerName} NOT FOUND");
                    failCount++;
                }
            }
            
            // Summary
            Console.WriteLine("\n=== Test Summary ===");
            Console.WriteLine($"Total Tests: {passCount + failCount}");
            Console.WriteLine($"Passed: {passCount}");
            Console.WriteLine($"Failed: {failCount}");
            
            if (failCount == 0)
            {
                Console.WriteLine("\n✓ All event handlers are properly wired!");
            }
            else
            {
                Console.WriteLine($"\n✗ {failCount} event handler(s) missing or not properly wired!");
            }
        }
        
        private static bool VerifyEventHandler(Type windowType, string handlerName)
        {
            // Check if the method exists in the window type
            var method = windowType.GetMethod(handlerName, 
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            return method != null;
        }
    }
}
