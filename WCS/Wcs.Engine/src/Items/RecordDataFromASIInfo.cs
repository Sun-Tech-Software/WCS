using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wcs.Engine.Items;

public class RecordDataFromAsiInfo
{

    #region Private Fields

    private bool _DECODE_LU_LABEL_CODES;

    private decimal _NEWLU_ID_LU_TYPE;

    private string? _NEWLU_ID_QT;

    private string? _NEWLU_ID_REFERENCE = string.Empty;

    private decimal _NEWLU_QT;

    private bool _RECORD_ID_LU_TYPE;

    private bool _RECORD_LU_CONTAINER_SIZE_HEIGTH;

    private bool _RECORD_LU_CONTAINER_SIZE_LENGTH;

    private bool _RECORD_LU_CONTAINER_SIZE_WIDTH;

    private bool _RECORD_LU_CONTAINER_TYPE;

    private bool _RECORD_LU_HEIGHT;

    private bool _RECORD_LU_LENGTH;

    private bool _RECORD_LU_SIDE1SURPLUS;

    private bool _RECORD_LU_SIDE2SURPLUS;

    private bool _RECORD_LU_WEIGTH;

    private bool _RECORD_LU_WIDTH;

    #endregion Private Fields

    #region Public Constructors

    public RecordDataFromAsiInfo()
    {
        ORM.RecordDataFromAsiInfo.Get(this);
    }

    #endregion Public Constructors

    #region Public Events

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion Public Events

    #region Public Properties

    public bool DECODE_LU_LABEL_CODES
    {
        get => _DECODE_LU_LABEL_CODES;
        set => SetProperty(ref _DECODE_LU_LABEL_CODES, value, true);
    }

    public decimal NEWLU_ID_LU_TYPE
    {
        get => _NEWLU_ID_LU_TYPE;
        set => SetProperty(ref _NEWLU_ID_LU_TYPE, value, true);
    }

    public string? NEWLU_ID_QT
    {
        get => _NEWLU_ID_QT;
        set => SetProperty(ref _NEWLU_ID_QT, value, true);
    }

    public string? NEWLU_ID_REFERENCE
    {
        get => _NEWLU_ID_REFERENCE;
        set => SetProperty(ref _NEWLU_ID_REFERENCE, value, true);
    }

    public decimal NEWLU_QT
    {
        get => _NEWLU_QT;
        set => SetProperty(ref _NEWLU_QT, value, true);
    }

    public bool RECORD_LU_CONTAINER_SIZE_HEIGTH
    {
        get => _RECORD_LU_CONTAINER_SIZE_HEIGTH;
        set => SetProperty(ref _RECORD_LU_CONTAINER_SIZE_HEIGTH, value, true);
    }

    public bool RECORD_LU_CONTAINER_SIZE_LENGTH
    {
        get => _RECORD_LU_CONTAINER_SIZE_LENGTH;
        set => SetProperty(ref _RECORD_LU_CONTAINER_SIZE_LENGTH, value, true);
    }

    public bool RECORD_LU_CONTAINER_SIZE_WIDTH
    {
        get => _RECORD_LU_CONTAINER_SIZE_WIDTH;
        set => SetProperty(ref _RECORD_LU_CONTAINER_SIZE_WIDTH, value, true);
    }

    public bool RECORD_LU_CONTAINER_TYPE
    {
        get => _RECORD_LU_CONTAINER_TYPE;
        set => SetProperty(ref _RECORD_LU_CONTAINER_TYPE, value, true);
    }

    public bool RECORD_LU_HEIGHT
    {
        get => _RECORD_LU_HEIGHT;
        set => SetProperty(ref _RECORD_LU_HEIGHT, value, true);
    }

    public bool RECORD_LU_LENGTH
    {
        get => _RECORD_LU_LENGTH;
        set => SetProperty(ref _RECORD_LU_LENGTH, value, true);
    }

    public bool RECORD_LU_SIDE1SURPLUS
    {
        get => _RECORD_LU_SIDE1SURPLUS;
        set => SetProperty(ref _RECORD_LU_SIDE1SURPLUS, value, true);
    }

    public bool RECORD_LU_SIDE2SURPLUS
    {
        get => _RECORD_LU_SIDE2SURPLUS;
        set => SetProperty(ref _RECORD_LU_SIDE2SURPLUS, value, true);
    }

    public bool RECORD_LU_TYPE
    {
        get => _RECORD_ID_LU_TYPE;
        set => SetProperty(ref _RECORD_ID_LU_TYPE, value, true);
    }

    public bool RECORD_LU_WEIGTH
    {
        get => _RECORD_LU_WEIGTH;
        set => SetProperty(ref _RECORD_LU_WEIGTH, value, true);
    }

    public bool RECORD_LU_WIDTH
    {
        get => _RECORD_LU_WIDTH;
        set => SetProperty(ref _RECORD_LU_WIDTH, value, true);
    }

    #endregion Public Properties

    #region Private Methods

    private void SetProperty<T>(ref T field, T value, bool updateDb,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (updateDb) ORM.RecordDataFromAsiInfo.Update(this);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion Private Methods

}