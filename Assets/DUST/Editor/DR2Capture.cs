using System.IO;
using UnityEngine;

namespace DUST.EditorTools
{
    // 把指定攝影機渲染成 PNG（給 lookdev 截圖用）
    public static class DR2Capture
    {
        public static void Shot(Camera cam, string path, int width = 1600, int height = 900, int frames = 8)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            for (int i = 0; i < frames; i++)
                cam.Render();
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            cam.targetTexture = prev;
            rt.Release();
            Object.DestroyImmediate(rt);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
