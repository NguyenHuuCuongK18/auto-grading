using System.ComponentModel;

namespace SolutionGrader.UI.Models;

/// <summary>
/// Represents a student's solution submission for grading.
/// </summary>
public class StudentSolution : INotifyPropertyChanged
{
    private bool _isSelected;
    private GradingStatus _status = GradingStatus.Not_Run;
    private double _mark;
    private double _maxMark;
    private string? _statusMessage;
    private int _progressPercent;
    
    /// <summary>
    /// Gets or sets the student's code/ID.
    /// </summary>
    public string StudentCode { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the paper number.
    /// </summary>
    public string PaperNo { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the path to the student's solution folder.
    /// </summary>
    public string SolutionPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the path to the client project (if applicable).
    /// </summary>
    public string? ClientPath { get; set; }
    
    /// <summary>
    /// Gets or sets the path to the server project (if applicable).
    /// </summary>
    public string? ServerPath { get; set; }
    
    /// <summary>
    /// Gets or sets whether this student is selected for grading.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }
    
    /// <summary>
    /// Gets or sets the grading status.
    /// </summary>
    public GradingStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }
    }
    
    /// <summary>
    /// Gets or sets the mark achieved.
    /// </summary>
    public double Mark
    {
        get => _mark;
        set
        {
            if (Math.Abs(_mark - value) > 0.001)
            {
                _mark = value;
                OnPropertyChanged(nameof(Mark));
            }
        }
    }
    
    /// <summary>
    /// Gets or sets the maximum possible mark.
    /// </summary>
    public double MaxMark
    {
        get => _maxMark;
        set
        {
            if (Math.Abs(_maxMark - value) > 0.001)
            {
                _maxMark = value;
                OnPropertyChanged(nameof(MaxMark));
            }
        }
    }
    
    /// <summary>
    /// Gets or sets a status message (e.g., error details).
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }
    }
    
    /// <summary>
    /// Gets or sets the progress percentage (0-100).
    /// </summary>
    public int ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (_progressPercent != value)
            {
                _progressPercent = Math.Clamp(value, 0, 100);
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }
    
    /// <summary>
    /// Gets or sets the grading start time.
    /// </summary>
    public DateTime? StartTime { get; set; }
    
    /// <summary>
    /// Gets or sets the grading end time.
    /// </summary>
    public DateTime? EndTime { get; set; }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
