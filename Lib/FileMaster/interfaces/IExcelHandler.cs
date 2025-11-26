using FileMaster.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

namespace FileMaster.Interfaces
{
    public interface IExcelHandler : IDisposable
    {
        int GetTotalRows(string sheetName);
        object GetCellData(string sheetName, int row, int column);
        object GetData(Type type, object data);
        List<string> GetAllSheetName();
        int GetAllSheetCount();
        IDictionary<string, int> GetTestCaseRowNo(string sheet, int column = 1);
        void SaveSheet(string filePath = "");
        void SaveSheet();
        void AddNewSheet(string sheetName);
        void WriteToCell(string sheetName, int row, int column, string value);
        void WriteToCell(string sheetName, int row, int column, string value, Action<CellStyle> builder);
        void AppendToCell(string sheetName, int column, string value);
        bool IsWorkSheetFound(string sheetName);
        DataTable GetDataTable(string sheetName);
        
    }
}
