using System.Net;

namespace .Helpers
{
    public static class StreamHelper
    {
        public static bool isStreamRunning = false;
        private static Stream _currentStream = null;
        private static HttpWebResponse response = null;

        public static async Task<Stream> StartOrGetStreamAsync(string ipAddress, string username, string password)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"http://{ipAddress}/ISAPI/Streaming/Channels/101/httpPreview");
                request.Credentials = new NetworkCredential(username, password);
                request.PreAuthenticate = true;
                response = (HttpWebResponse)await request.GetResponseAsync();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    _currentStream = response.GetResponseStream();
                    isStreamRunning = true;
                    return _currentStream;
                }
                else
                {
                    throw new Exception($"Failed to access stream: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error occurred: {ex.Message}");
            }
        }

        public static void StopStream()
        {
            _currentStream?.Dispose();
            response?.Dispose();
            isStreamRunning = false;

        }
    }
}