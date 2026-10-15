using Wcs.Api.Contracts.Dtos;

namespace Wcs.Viewer.Items
{
    public class AutomationJunction
    {

        #region Public Properties

        public AutomationJunction()
        {

        }

        public Guid DestinationId { get; set; }

        public AutomationDevice? DestinationItem
        {
            get
            {
                return Navigator.PathDevices.FirstOrDefault(x => x is not null && x.ItemGuid == DestinationId);
            }
            //set
            //{
            //    if (value == null)
            //        DestinationId = Guid.Empty;
            //    else
            //        DestinationId = value.ItemGuid;
            //}
        }

        public Guid ItemGuid { get; set; }

        public int Priority { get; set; }

        public Guid SourceId { get; set; }

        public AutomationDevice? SourceItem
        {
            get
            {
                return Navigator.PathDevices.FirstOrDefault(x => x is not null && x.ItemGuid == SourceId);
            }
            //set
            //{
            //    if (value == null)
            //        SourceId = Guid.Empty;
            //    else
            //        SourceId = value.ItemGuid;
            //}
        }

        #endregion Public Properties

        #region Public Methods

        /// <summary>Crea la giunzione a partire dal DTO del piano.</summary>
        public static AutomationJunction From(JunctionDto dto) => new()
        {
            ItemGuid = dto.JunctionId,
            SourceId = dto.SourceItemId,
            DestinationId = dto.DestinationItemId,
            Priority = dto.Priority,
        };

        #endregion Public Methods
    }
}
