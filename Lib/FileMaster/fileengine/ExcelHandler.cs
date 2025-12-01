using FileMaster.Interfaces;
using OfficeOpenXml.Style;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using FileMaster.Models;
using System.Drawing;

namespace FileMaster.FileEngine
{
    public class ExcelHandler : IExcelHandler
    {
        #region Fields
        private ExcelPackage _package { get; set; }
        #endregion

        #region Constructors
        public ExcelHandler()
        {

        }
        public ExcelHandler(string fileName) : this(new FileInfo(fileName))
        {

        }
        public ExcelHandler(FileInfo fileInfo)
        {
            ExcelPackage.License.SetNonCommercialOrganization("Nihao");
            _package = new ExcelPackage(fileInfo);
        }
        #endregion

        #region Public
        public int GetAllSheetCount()
        {
            return _package.Workbook.Worksheets.Count;
        }

        public List<string> GetAllSheetName()
        {
            return _package.Workbook.Worksheets.Select(sheet => sheet.Name).ToList();
        }

        public object GetCellData(string sheetName, int row, int column)
        {
            return _package.Workbook.Worksheets[sheetName].Cells[row, column].Text;
        }

        public object GetData(Type type, object data)
        {
            switch (type.Name)
            {
                case "String":
                    return data.ToString();
                case "Double":
                    return Convert.ToDouble(data);
                case "DateTime":
                    return Convert.ToDateTime(data);
                default:
                    return data.ToString();
            }
        }

        public IDictionary<string, int> GetTestCaseRowNo(string sheet, int column = 3)
        {
            var totalRows = GetTotalRows(sheet);
            var testCaseId = new Dictionary<string, int>();

            for (var i = 2; i <= totalRows; i++)
            {
                var celValue = GetCellData(sheet, i, column);
                if (!testCaseId.ContainsKey((string)celValue))
                {
                    testCaseId.Add((string)celValue, i);
                }
            }
            return testCaseId;
        }

        public int GetTotalRows(string sheetName)
        {
            var count = 1;

            while (!GetCellData(sheetName, count, 1).Equals(string.Empty))
            {
                count++;
            }

            return --count;
        }

        public void SaveSheet(string filePath)
        {
            _package.SaveAs(filePath);
        }

        public void SaveSheet()
        {
            _package.Save();
        }

        public void WriteToCell(string sheetName, int row, int column, string value)
        {
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Value = value;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Font.Bold = false;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Fill.PatternType = ExcelFillStyle.Solid;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Top.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Right.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Bottom.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Left.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.White);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].AutoFitColumns();
        }

        public bool IsWorkSheetFound(string sheetName)
        {
            var workSheet = _package.Workbook.Worksheets[sheetName];
            if (workSheet != null)
                return true;
            return false;
        }
        public System.Data.DataTable GetDataTable(string sheetName)
        {
            var workSheet = _package.Workbook.Worksheets[sheetName];
            DataTable data = workSheet.Cells[1, 1, workSheet.Dimension.End.Row, workSheet.Dimension.End.Column].ToDataTable();
            return data;
        }

        public void CreateSheet(string sheetName)
        {
            var workSheet = _package.Workbook.Worksheets.Add(sheetName);
        }

        public void WriteToCells(int fromRow, int fromCol, int toRow, int toCol, string text, string sheetName)
        {
            _package.Workbook.Worksheets[sheetName].Cells[fromRow, fromCol].Value = text;
            _package.Workbook.Worksheets[sheetName].Cells[fromRow, fromCol].AutoFitColumns();
            _package.Workbook.Worksheets[sheetName].Cells[fromRow, fromCol, toRow, toCol].Merge = true;
        }
        public void AppendToCell(string sheetName, int column, string value)
        {
            int lastRow = _package.Workbook.Worksheets[sheetName].Dimension.End.Row;
            WriteToCell(sheetName, lastRow, column, value);
        }
        public void AddNewSheet(string sheetName)
        {
            _package.Workbook.Worksheets.Add(sheetName);
        }
        public void WriteToCell(string sheetName, int row, int column, string value, Action<CellStyle> builder)
        {
            CellStyle cellStyle = new CellStyle();
            builder(cellStyle);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Value = value;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Font.Bold = cellStyle.FontBold;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Top.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Right.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Bottom.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Border.Left.Color.SetColor(Color.Black);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Fill.PatternType = cellStyle.PatterntType;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.Fill.BackgroundColor.SetColor(cellStyle.BackgroundColor);
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.WrapText = cellStyle.WrapText;
            _package.Workbook.Worksheets[sheetName].Cells[row, column].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
            _package.Workbook.Worksheets[sheetName].Cells.AutoFitColumns();
        }
        #endregion 

        #region Dispose
        public void Dispose()
        {
            _package.Dispose();
        }

        #endregion
    }
}
