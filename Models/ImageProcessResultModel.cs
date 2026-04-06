
using System.Collections.Generic;

namespace Models
{
    public class ImageProcessResultModel
    {
        public int UnitImageId { get; set; }
        
        public string ImageToAuthorize { get; set; }
        
        public List<UnitImageInspectionModel> UnitImageInspections { get; set; }

        public ResultMessageModel ResultMessage { get; set; }
    }
}