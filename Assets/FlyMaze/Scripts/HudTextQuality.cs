using System;
using UnityEngine;
using UnityEngine.UI;

namespace FlyMaze
{
    internal static class HudTextQuality
    {
        private const float Supersample = 2f;
        private static Font _runtimeFont;

        public static void Apply(Transform root)
        {
            if (root == null)
                return;

            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
                canvases[i].pixelPerfect = true;

            Font font = ResolveFont();
            Text[] texts = root.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < texts.Length; i++)
                UpgradeText(texts[i], font);
        }

        private static Font ResolveFont()
        {
            if (_runtimeFont != null)
                return _runtimeFont;

            string[] preferred =
            {
                "Segoe UI Semibold",
                "Segoe UI",
                "Arial",
                "Noto Sans",
                "Liberation Sans"
            };

            string[] installed = Font.GetOSInstalledFontNames();
            for (int p = 0; p < preferred.Length; p++)
            {
                for (int i = 0; i < installed.Length; i++)
                {
                    if (!string.Equals(installed[i], preferred[p], StringComparison.OrdinalIgnoreCase))
                        continue;

                    _runtimeFont = Font.CreateDynamicFontFromOSFont(installed[i], 64);
                    if (_runtimeFont != null)
                        break;
                }

                if (_runtimeFont != null)
                    break;
            }

            if (_runtimeFont == null)
                _runtimeFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (_runtimeFont != null && _runtimeFont.material != null && _runtimeFont.material.mainTexture != null)
                _runtimeFont.material.mainTexture.filterMode = FilterMode.Bilinear;

            return _runtimeFont;
        }

        private static void UpgradeText(Text text, Font font)
        {
            if (text == null)
                return;

            RectTransform rect = text.rectTransform;
            if (rect == null)
                return;

            // Render each glyph at 2x its logical size, then scale the RectTransform back down.
            // Legacy UGUI gets a much denser font atlas this way without adding a font package.
            bool stretched = rect.anchorMin != rect.anchorMax;
            if (stretched && rect.parent is RectTransform parentRect)
            {
                Vector2 parentSize = parentRect.rect.size;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = parentSize * Supersample;
            }
            else
            {
                rect.sizeDelta *= Supersample;
            }

            rect.localScale *= 1f / Supersample;

            if (font != null)
                text.font = font;

            text.fontSize = Mathf.Max(1, Mathf.RoundToInt(text.fontSize * Supersample));
            text.alignByGeometry = true;
            text.resizeTextForBestFit = false;
        }
    }
}
