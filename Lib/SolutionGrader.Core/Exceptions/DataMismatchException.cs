using System;

namespace SolutionGrader.Core.Exceptions
{
    /// <summary>
    /// Exception thrown when database query results don't match expected data
    /// </summary>
    public class DataMismatchException : Exception
    {
        public DataMismatchException(string message) : base(message)
        {
        }

        public DataMismatchException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }
}
