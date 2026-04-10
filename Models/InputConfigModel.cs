using System.Collections.Generic;

namespace Models
{
    public class InputConfigModel
    {
        public List<CameraControlItemAndImagePairModel> ImagePairs { get; set; }
        public List<CameraControlItemInspectionModel> Inspections { get; set; }
    }
}
