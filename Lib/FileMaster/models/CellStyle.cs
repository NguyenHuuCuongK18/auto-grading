using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FileMaster.Models
{
    /// <summary>
    /// Cell style configuration for Excel cells.
    /// Uses ARGB byte values instead of System.Drawing.Color for cross-platform compatibility.
    /// </summary>
    public class CellStyle
    {
        public bool FontBold { get; set; } = false;
        public bool FontItalic { get; set; } = false;
        
        /// <summary>
        /// Background color Alpha component (0-255)
        /// </summary>
        public byte BackgroundColorA { get; set; } = 255;
        /// <summary>
        /// Background color Red component (0-255)
        /// </summary>
        public byte BackgroundColorR { get; set; } = 255;
        /// <summary>
        /// Background color Green component (0-255)
        /// </summary>
        public byte BackgroundColorG { get; set; } = 255;
        /// <summary>
        /// Background color Blue component (0-255)
        /// </summary>
        public byte BackgroundColorB { get; set; } = 255;
        
        /// <summary>
        /// Font color Red component (0-255)
        /// </summary>
        public byte FontColorR { get; set; } = 0;
        /// <summary>
        /// Font color Green component (0-255)
        /// </summary>
        public byte FontColorG { get; set; } = 0;
        /// <summary>
        /// Font color Blue component (0-255)
        /// </summary>
        public byte FontColorB { get; set; } = 0;
        
        public ExcelFillStyle PatterntType { get; set; } = ExcelFillStyle.Solid;
        public bool WrapText { get; set; }

        /// <summary>
        /// Set background color using ARGB values
        /// </summary>
        public void SetBackgroundColor(byte a, byte r, byte g, byte b)
        {
            BackgroundColorA = a;
            BackgroundColorR = r;
            BackgroundColorG = g;
            BackgroundColorB = b;
        }

        public static Action<CellStyle> GetStatusStyle(int status = 5)
        {
            // Define colors as ARGB values (A, R, G, B)
            byte a = 255;
            byte r = 255, g = 255, b = 255; // Default white
            
            switch (status)
            {
                case 0: // Green
                    r = 0; g = 128; b = 0;
                    break;
                case 4:
                case 1: // Red
                    r = 255; g = 0; b = 0;
                    break;
                case 2: // Yellow
                    r = 255; g = 255; b = 0;
                    break;
                case 3: // ForestGreen
                    r = 34; g = 139; b = 34;
                    break;
                default: // White
                    r = 255; g = 255; b = 255;
                    break;
            }
            return (x) =>
            {
                x.SetBackgroundColor(a, r, g, b);
                x.FontBold = true;
            };
        }

    
    }
}
