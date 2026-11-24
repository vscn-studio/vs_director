// Adapted from VS-6DOF (DF; bjfishman / TGU-HansJack), CC0-1.0.
// See licenses/VS-6DOF-CC0.txt and licenses/THIRD-PARTY.txt.
#nullable disable
namespace VSDirector.HeadTracking
{
    public class HeadTrackingConfig
    {
        public HeadTrackingConfig Copy() => (HeadTrackingConfig)MemberwiseClone();
        public bool EnableTracking = false;
        public float SmoothingSeconds = 0.04f;
        public bool EnableRotation = true;
        public bool EnableTranslation = true;
        public bool EnableRoll = true;
        public bool EnableCrouchToggle = true;
        public bool DisablePlayerModel = false;

        public float YawGain = 2.0f;
        public float PitchGain = 2.2f;
        public float RollGain = 80.0f;
        public float TranslationGain = 0.5f;
        public float MaxTranslation = 0.5f;
        public float TranslationGainX = 0.5f;
        public float TranslationGainY = 1.0f;
        public float TranslationGainZ = 0.5f;
        public float MaxTranslationX = 0.45f;
        public float MaxTranslationY = 0.4f;
        public float MaxTranslationZ = 0.4f;
        public float BaselineOffsetX = 0.0f;
        public float BaselineOffsetY = 0.3f;
        public float BaselineOffsetZ = -0.5f;

        public float CrouchThreshold = -1.2f;
        public float CrouchHysteresis = 0.05f;
        public string CrouchMode = "hold";
        public string CrouchAxis = "Y";

        public bool EnableLeanToZoom = false;
        public bool EnableZoomVignette = true;
        public float ZoomFov = 20;
        public float ZoomSensitivity = 0.5f;
        public bool ChangeZoomSmoothing = false;
        public float ZoomSmoothing = 0;
        public float LeanToZoomThreshold = 0.2f;
        public float LeanToZoomHysteresis = 0.05f;
        public bool EnableLeanToZoomAxis = false;
        public float LeanToZoomAxisRange = 0.2f;
        public float LeanToZoomAxisMax = 1.0f;
    }
}
