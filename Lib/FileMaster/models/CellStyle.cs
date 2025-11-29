using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FileMaster.Models
{
    public class CellStyle
    {
        public bool FontBold { get; set; } = false;
        public bool FontItalic { get; set; } = false;
        public Color BackgroundColor { get; set; } = Color.White;
        public Color FontColor { get; set; }
        public ExcelFillStyle PatterntType { get; set; } = ExcelFillStyle.Solid;
        public bool WrapText { get; set; }

        public static Action<CellStyle> GetStatusStyle(int status = 5)
        {
            Color color;
            switch (status)
            {
                case 0:
                    color = Color.Green;
                    break;
                case 4:
                case 1:
                    color = Color.Red;
                    break;
                case 2:
                    color = Color.Yellow;
                    break;
                case 3:
                    color = Color.ForestGreen;
                    break;
                default:
                    color = Color.White;
                    break;
            }
            return (x) =>
            {
                x.BackgroundColor = color;
                x.FontBold = true;
            };
        }

    
    }
}
