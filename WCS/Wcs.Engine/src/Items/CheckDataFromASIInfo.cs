using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wcs.Engine.Items;

public class CheckDataFromAsiInfo
{

    #region Private Fields

    private CompareTypes _COMP_LU_CODE;

    private CompareTypes _COMP_LU_CONTAINER_SIZE_HEIGTH;

    private CompareTypes _COMP_LU_CONTAINER_SIZE_LENGTH;

    private CompareTypes _COMP_LU_CONTAINER_SIZE_WIDTH;

    private CompareTypes _COMP_LU_CONTAINER_TYPE;

    private CompareTypes _COMP_LU_HEIGHT;

    private CompareTypes _COMP_LU_HEIGTH_SURPLUS;

    private CompareTypes _COMP_LU_LENGTH;

    private CompareTypes _COMP_LU_LENGTH_SIDE_1_SURPLUS;

    private CompareTypes _COMP_LU_LENGTH_SIDE_2_SURPLUS;

    private CompareTypes _COMP_LU_TEMP;

    private CompareTypes _COMP_LU_WEIGTH;

    private CompareTypes _COMP_LU_WIDTH;

    private CompareTypes _COMP_LU_WIDTH_SIDE_1_SURPLUS;

    private CompareTypes _COMP_LU_WIDTH_SIDE_2_SURPLUS;

    private string? _LU_CODE = string.Empty;

    private int _LU_CONTAINER_SIZE_HEIGTH;

    private int _LU_CONTAINER_SIZE_LENGTH;

    private int _LU_CONTAINER_SIZE_WIDTH;

    private int _LU_CONTAINER_TYPE;

    private int _LU_HEIGHT;

    private int _LU_HEIGTH_SURPLUS;

    private int _LU_LENGTH;

    private int _LU_LENGTH_SIDE_1_SURPLUS;

    private int _LU_LENGTH_SIDE_2_SURPLUS;

    private int _LU_TEMP;

    private int _LU_WEIGTH;

    private int _LU_WIDTH;

    private int _LU_WIDTH_SIDE_1_SURPLUS;

    private int _LU_WIDTH_SIDE_2_SURPLUS;

    #endregion Private Fields

    #region Public Constructors

    public CheckDataFromAsiInfo()
    {
        ORM.CheckDataFromAsiInfo.Get(this);
    }

    #endregion Public Constructors

    #region Public Events

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion Public Events

    #region Public Enums

    public enum CompareTypes : byte
    {
        NoComparision = 0,
        NotEqual = 1,
        Equal = 2,
        MoreThan = 3,
        LessThan = 4
    }

    #endregion Public Enums

    #region Public Properties

    public CompareTypes COMP_LU_CODE
    {
        get => _COMP_LU_CODE;
        set => SetProperty(ref _COMP_LU_CODE, value, true);
    }

    public CompareTypes COMP_LU_CONTAINER_SIZE_HEIGTH
    {
        get => _COMP_LU_CONTAINER_SIZE_HEIGTH;
        set => SetProperty(ref _COMP_LU_CONTAINER_SIZE_HEIGTH, value, true);
    }

    public CompareTypes COMP_LU_CONTAINER_SIZE_LENGTH
    {
        get => _COMP_LU_CONTAINER_SIZE_LENGTH;
        set => SetProperty(ref _COMP_LU_CONTAINER_SIZE_LENGTH, value, true);
    }

    public CompareTypes COMP_LU_CONTAINER_SIZE_WIDTH
    {
        get => _COMP_LU_CONTAINER_SIZE_WIDTH;
        set => SetProperty(ref _COMP_LU_CONTAINER_SIZE_WIDTH, value, true);
    }

    public CompareTypes COMP_LU_CONTAINER_TYPE
    {
        get => _COMP_LU_CONTAINER_TYPE;
        set => SetProperty(ref _COMP_LU_CONTAINER_TYPE, value, true);
    }

    public CompareTypes COMP_LU_HEIGHT
    {
        get => _COMP_LU_HEIGHT;
        set => SetProperty(ref _COMP_LU_HEIGHT, value, true);
    }

    public CompareTypes COMP_LU_HEIGTH_SURPLUS
    {
        get => _COMP_LU_HEIGTH_SURPLUS;
        set => SetProperty(ref _COMP_LU_HEIGTH_SURPLUS, value, true);
    }

    public CompareTypes COMP_LU_LENGTH
    {
        get => _COMP_LU_LENGTH;
        set => SetProperty(ref _COMP_LU_LENGTH, value, true);
    }

    public CompareTypes COMP_LU_LENGTH_SIDE_1_SURPLUS
    {
        get => _COMP_LU_LENGTH_SIDE_1_SURPLUS;
        set => SetProperty(ref _COMP_LU_LENGTH_SIDE_1_SURPLUS, value, true);
    }

    public CompareTypes COMP_LU_LENGTH_SIDE_2_SURPLUS
    {
        get => _COMP_LU_LENGTH_SIDE_2_SURPLUS;
        set => SetProperty(ref _COMP_LU_LENGTH_SIDE_2_SURPLUS, value, true);
    }

    public CompareTypes COMP_LU_TEMP
    {
        get => _COMP_LU_TEMP;
        set => SetProperty(ref _COMP_LU_TEMP, value, true);
    }

    public CompareTypes COMP_LU_WEIGTH
    {
        get => _COMP_LU_WEIGTH;
        set => SetProperty(ref _COMP_LU_WEIGTH, value, true);
    }

    public CompareTypes COMP_LU_WIDTH
    {
        get => _COMP_LU_WIDTH;
        set => SetProperty(ref _COMP_LU_WIDTH, value, true);
    }

    public CompareTypes COMP_LU_WIDTH_SIDE_1_SURPLUS
    {
        get => _COMP_LU_WIDTH_SIDE_1_SURPLUS;
        set => SetProperty(ref _COMP_LU_WIDTH_SIDE_1_SURPLUS, value, true);
    }

    public CompareTypes COMP_LU_WIDTH_SIDE_2_SURPLUS
    {
        get => _COMP_LU_WIDTH_SIDE_2_SURPLUS;
        set => SetProperty(ref _COMP_LU_WIDTH_SIDE_2_SURPLUS, value, true);
    }

    public string? LU_CODE
    {
        get => _LU_CODE;
        set => SetProperty(ref _LU_CODE, value, true);
    }

    public int LU_CONTAINER_SIZE_HEIGTH
    {
        get => _LU_CONTAINER_SIZE_HEIGTH;
        set => SetProperty(ref _LU_CONTAINER_SIZE_HEIGTH, value, true);
    }

    public int LU_CONTAINER_SIZE_LENGTH
    {
        get => _LU_CONTAINER_SIZE_LENGTH;
        set => SetProperty(ref _LU_CONTAINER_SIZE_LENGTH, value, true);
    }

    public int LU_CONTAINER_SIZE_WIDTH
    {
        get => _LU_CONTAINER_SIZE_WIDTH;
        set => SetProperty(ref _LU_CONTAINER_SIZE_WIDTH, value, true);
    }

    public int LU_CONTAINER_TYPE
    {
        get => _LU_CONTAINER_TYPE;
        set => SetProperty(ref _LU_CONTAINER_TYPE, value, true);
    }

    public int LU_HEIGHT
    {
        get => _LU_HEIGHT;
        set => SetProperty(ref _LU_HEIGHT, value, true);
    }

    public int LU_HEIGTH_SURPLUS
    {
        get => _LU_HEIGTH_SURPLUS;
        set => SetProperty(ref _LU_HEIGTH_SURPLUS, value, true);
    }

    public int LU_LENGTH
    {
        get => _LU_LENGTH;
        set => SetProperty(ref _LU_LENGTH, value, true);
    }

    public int LU_LENGTH_SIDE_1_SURPLUS
    {
        get => _LU_LENGTH_SIDE_1_SURPLUS;
        set => SetProperty(ref _LU_LENGTH_SIDE_1_SURPLUS, value, true);
    }

    public int LU_LENGTH_SIDE_2_SURPLUS
    {
        get => _LU_LENGTH_SIDE_2_SURPLUS;
        set => SetProperty(ref _LU_LENGTH_SIDE_2_SURPLUS, value, true);
    }

    public int LU_TEMP
    {
        get => _LU_TEMP;
        set => SetProperty(ref _LU_TEMP, value, true);
    }

    public int LU_WEIGTH
    {
        get => _LU_WEIGTH;
        set => SetProperty(ref _LU_WEIGTH, value, true);
    }

    public int LU_WIDTH
    {
        get => _LU_WIDTH;
        set => SetProperty(ref _LU_WIDTH, value, true);
    }

    public int LU_WIDTH_SIDE_1_SURPLUS
    {
        get => _LU_WIDTH_SIDE_1_SURPLUS;
        set => SetProperty(ref _LU_WIDTH_SIDE_1_SURPLUS, value, true);
    }

    public int LU_WIDTH_SIDE_2_SURPLUS
    {
        get => _LU_WIDTH_SIDE_2_SURPLUS;
        set => SetProperty(ref _LU_WIDTH_SIDE_2_SURPLUS, value, true);
    }

    #endregion Public Properties

    #region Private Methods

    private void SetProperty<T>(ref T field, T value, bool updateDb,
                                                                                                                                [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (updateDb) ORM.CheckDataFromAsiInfo.Update(this);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion Private Methods

}