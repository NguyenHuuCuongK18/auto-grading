using FileMaster.FileEngine;
using FileMaster.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SolutionGrader.Services
{
    public class EnvironmentService
    {
        private static string StepsSheet = "Run";
        private static string ConfigsSheet = "Config";
        private static int StepsCol = 1;
        private static int KeywordActionCol = 2;
        private static int KeyCol = 1;
        private static int ValueCol = 2;

        public static Domain.Entities.Main.Environment GetEnvironment(string file)
        {
            Domain.Entities.Main.Environment env = new Domain.Entities.Main.Environment();
            IExcelHandler envFile = new ExcelHandler(file);
            Dictionary<string, string> configs = GetConfigs(envFile);
            List<string> steps = GetSteps(envFile);
            env.Steps = steps;
            env.Configs = configs;
            return env;

        }

        private static List<string> GetSteps(IExcelHandler envFile)
        {
            List<string> steps = new List<string>();
            if (envFile.IsWorkSheetFound(StepsSheet))
            {
                var i = 2;
                while (!String.IsNullOrEmpty(((string)envFile.GetCellData(StepsSheet, i, StepsCol)))) 
                {

                    string action = (string)envFile.GetCellData(StepsSheet, i, KeywordActionCol);
                    steps.Add(action);
                    i++;
                }
            }
            return steps;
        }

        private static Dictionary<string, string> GetConfigs(IExcelHandler envFile)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (envFile.IsWorkSheetFound(ConfigsSheet))
            {
                var i = 2;
                while (!String.IsNullOrEmpty(((string)envFile.GetCellData(ConfigsSheet, i, KeyCol))))
                {
                    string key = (string)envFile.GetCellData(ConfigsSheet, i, KeyCol);
                    string value = (string)envFile.GetCellData(ConfigsSheet, i, ValueCol);
                    result.Add(key, value);
                    i++;
                }
            }
            return result;
        }
    }
}
