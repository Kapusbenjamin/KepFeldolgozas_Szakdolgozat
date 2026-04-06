using Enums;

namespace Models
{
    public class CameraControlItemInspectionModel
    {
        public int Id { get; set; }

        public string ControlItemName { get; set; }

        public double InitialXOffset { get; set; }

        public double InitialYOffset { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public int Offset { get; set; }

        public string RequiredValue { get; set; }

        public int? Angle { get; set; }

        public int StrokeWidth { get; set; }

        public string StrokeColor { get; set; }

        public string FillColor { get; set; }

        public int Degree { get; set; }

        public int BorderRadius { get; set; } 

        public double DimensionX { get; set; }

        public double DimensionY { get; set; }

        public InspectionType InspectionType { get; set; }

        public int PTZCameraControlItemId { get; set; }

        public int UploadedFileId { get; set; }

        public bool Updated { get; set; }

        public bool LeaderApprove { get; set; }

        public bool Active { get; set; }

        public int OriginalImgWidth { get; set; }

        public int OriginalImgHeight { get; set; }

        public int? ArUcoCorner1X { get; set; }

        public int? ArUcoCorner1Y { get; set; }

        public int? ArUcoCorner2X { get; set; }

        public int? ArUcoCorner2Y { get; set; }

        public int? ArUcoCorner3X { get; set; }

        public int? ArUcoCorner3Y { get; set; }

        public int? ArUcoCorner4X { get; set; }

        public int? ArUcoCorner4Y { get; set; }
    }
}