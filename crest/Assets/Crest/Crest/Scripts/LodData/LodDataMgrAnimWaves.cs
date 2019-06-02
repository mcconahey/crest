﻿// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Crest
{
    /// <summary>
    /// Captures waves/shape that is drawn kinematically - there is no frame-to-frame state. The Gerstner
    /// waves are drawn in this way. There are two special features of this particular LodData.
    ///
    ///  * A combine pass is done which combines downwards from low detail LODs down into the high detail LODs (see OceanScheduler).
    ///  * The textures from this LodData are passed to the ocean material when the surface is drawn (by OceanChunkRenderer).
    ///  * LodDataDynamicWaves adds its results into this LodData. The dynamic waves piggy back off the combine
    ///    pass and subsequent assignment to the ocean material (see OceanScheduler).
    ///  * The LodDataSeaFloorDepth sits on this same GameObject and borrows the camera. This could be a model for the other sim types..
    /// </summary>
    public class LodDataMgrAnimWaves : LodDataMgr, IFloatingOrigin
    {
        public override string SimName { get { return "AnimatedWaves"; } }
        // shape format. i tried RGB111110Float but error becomes visible. one option would be to use a UNORM setup.
        public override RenderTextureFormat TextureFormat { get { return RenderTextureFormat.ARGBHalf; } }
        protected override bool NeedToReadWriteTextureData { get { return true; } }

        [Tooltip("Read shape textures back to the CPU for collision purposes.")]
        public bool _readbackShapeForCollision = true;

        /// <summary>
        /// Turn shape combine pass on/off. Debug only - ifdef'd out in standalone
        /// </summary>
        public static bool _shapeCombinePass = true;

        List<ShapeGerstnerBatched> _gerstnerComponents = new List<ShapeGerstnerBatched>();

        RenderTexture _waveBuffers;

        const string ShaderName = "ShapeCombine";
        // NOTE: These values are determined and must match the order in which
        // different kernel variants are defined in `ShapeCombine.compute`.
        const int CombineShaderKernel = 0;
        const int CombineShaderKernel_CombineDisabled = 1;
        ComputeShader _combineShader;
        PropertyWrapperCompute[] _combineProperties;

        public override void UseSettings(SimSettingsBase settings) { OceanRenderer.Instance._simSettingsAnimatedWaves = settings as SimSettingsAnimatedWaves; }
        public override SimSettingsBase CreateDefaultSettings()
        {
            var settings = ScriptableObject.CreateInstance<SimSettingsAnimatedWaves>();
            settings.name = SimName + " Auto-generated Settings";
            return settings;
        }

        protected override void InitData()
        {
            base.InitData();

            // Setup the RenderTexture and compute shader for combining
            // different animated wave LODs. As we use a single texture array
            // for all LODs, we employ a compute shader as only they can
            // read and write to the same texture.
            _combineShader = Resources.Load<ComputeShader>(ShaderName);
            _combineProperties = new PropertyWrapperCompute[OceanRenderer.Instance.CurrentLodCount];
            for (int i = 0; i < _combineProperties.Length; i++)
            {
                _combineProperties[i] = new PropertyWrapperCompute();
            }

            Debug.Assert(SystemInfo.SupportsRenderTextureFormat(TextureFormat), "The graphics device does not support the render texture format " + TextureFormat.ToString());
            // TODO(MRT): Is this the best place to put these? Or should we put support querying inside some global init?
            // Or a function the user can query themselves? Or even in an editor script? All three?
            Debug.Assert(SystemInfo.supports2DArrayTextures, "The graphics device does not support 2D array textures");
            Debug.Assert(SystemInfo.supportsComputeShaders, "The graphics device does not support comptue shaders");

            int resolution = OceanRenderer.Instance.LodDataResolution;
            var desc = new RenderTextureDescriptor(resolution, resolution, TextureFormat, 0);

            _waveBuffers = new RenderTexture(desc);
            _waveBuffers.wrapMode = TextureWrapMode.Clamp;
            _waveBuffers.antiAliasing = 1;
            _waveBuffers.filterMode = FilterMode.Bilinear;
            _waveBuffers.anisoLevel = 0;
            _waveBuffers.useMipMap = false;
            _waveBuffers.name = "WaveBuffer";
            _waveBuffers.dimension = TextureDimension.Tex2DArray;
            _waveBuffers.volumeDepth = OceanRenderer.Instance.CurrentLodCount;
        }

        // Filter object for assigning shapes to LODs. This was much more elegant with a lambda but it generated garbage.
        public class FilterWavelength : IDrawFilter
        {
            public float _lodMinWavelength;
            public float _lodMaxWavelength;
            public int _lodIdx;
            public int _lodCount;

            public bool Filter(RegisterLodDataInputBase data)
            {
                var drawOctaveWavelength = (data as RegisterAnimWavesInput).OctaveWavelength;
                return (_lodMinWavelength <= drawOctaveWavelength) && (drawOctaveWavelength < _lodMaxWavelength || _lodIdx == _lodCount - 1);
            }
        }
        FilterWavelength _filterWavelength = new FilterWavelength();

        public class FilterNoLodPreference : IDrawFilter
        {
            public bool Filter(RegisterLodDataInputBase data)
            {
                return (data as RegisterAnimWavesInput).OctaveWavelength == 0f;
            }
        }
        FilterNoLodPreference _filterNoLodPreference = new FilterNoLodPreference();

        public override void BuildCommandBuffer(OceanRenderer ocean, CommandBuffer buf)
        {
            base.BuildCommandBuffer(ocean, buf);

            var lodCount = OceanRenderer.Instance.CurrentLodCount;

            // lod-dependent data
            _filterWavelength._lodCount = lodCount;

            // TODO(MRT): Do this all in a single (geometry) shader call
            for (int lodIdx = lodCount - 1; lodIdx >= 0; lodIdx--)
            {
                buf.SetRenderTarget(_waveBuffers, 0, CubemapFace.Unknown, lodIdx);
                buf.ClearRenderTarget(false, true, Color.black);

                foreach (var gerstner in _gerstnerComponents)
                {
                    gerstner.BuildCommandBuffer(lodIdx, ocean, buf);
                }

                // draw any data with lod preference
                _filterWavelength._lodIdx = lodIdx;
                _filterWavelength._lodMaxWavelength = OceanRenderer.Instance._lods[lodIdx].MaxWavelength();
                _filterWavelength._lodMinWavelength = _filterWavelength._lodMaxWavelength / 2f;
                SubmitDrawsFiltered(lodIdx, buf, _filterWavelength);
            }

            // combine waves
            for (int lodIdx = lodCount - 1; lodIdx >= 0; lodIdx--)
            {
                // this lod data
                _combineProperties[lodIdx].SetFloat(OceanRenderer.Instance.SP_ThisLODSliceIndex, lodIdx);
                BindWaveBuffer(_combineProperties[lodIdx]);
                // combine data from next larger lod into this one
                BindResultData(_combineProperties[lodIdx]);

                // TODO(MRT): See if we can find a programmatic solution to also
                // select a kernel variant depending on if flow or dynamic waves
                // are enabled.
                int combineShaderKernel;
                if (lodIdx < lodCount - 1 && _shapeCombinePass)
                {
                    combineShaderKernel = CombineShaderKernel;
                }
                else
                {
                    combineShaderKernel = CombineShaderKernel_CombineDisabled;
                }

                // dynamic waves
                if (OceanRenderer.Instance._lodDataDynWaves)
                {
                    OceanRenderer.Instance._lodDataDynWaves.BindCopySettings(_combineProperties[lodIdx]);
                    OceanRenderer.Instance._lodDataDynWaves.BindResultData(_combineProperties[lodIdx]);
                }
                else
                {
                    LodDataMgrDynWaves.BindNull(_combineProperties[lodIdx]);
                }

                // flow
                if (OceanRenderer.Instance._lodDataFlow)
                {
                    OceanRenderer.Instance._lodDataFlow.BindResultData(_combineProperties[lodIdx]);
                }
                else
                {
                    LodDataMgrFlow.BindNull(_combineProperties[lodIdx]);
                }

                _combineProperties[lodIdx].InitialiseAndDispatchShader(
                    buf,
                    _combineShader, combineShaderKernel,
                    DataTexture
                );
            }

            // lod-independent data
            for (int lodIdx = lodCount - 1; lodIdx >= 0; lodIdx--)
            {
                buf.SetRenderTarget(_targets, 0, CubemapFace.Unknown, lodIdx);

                // draw any data that did not express a preference for one lod or another
                SubmitDrawsFiltered(lodIdx, buf, _filterNoLodPreference);
            }
        }

        public void BindWaveBuffer(IPropertyWrapper properties, bool prevFrame = false)
        {
            // TODO(MRT): See if there is a better way to validate all lods at
            // once.
            for(int lodIdx = 0; lodIdx < OceanRenderer.Instance.CurrentLodCount; lodIdx++)
            {
                LodTransform._staticRenderData[lodIdx].Validate(0, this);
            }
            properties.SetTexture(Shader.PropertyToID("_LD_TexArray_WaveBuffer_ThisFrame"), _waveBuffers);
            BindData(properties, null, true, ref LodTransform._staticRenderData, prevFrame);
        }

        protected override void BindData(IPropertyWrapper properties, Texture applyData, bool blendOut, ref LodTransform.RenderData[] renderData, bool prevFrame = false)
        {
            base.BindData(properties, applyData, blendOut, ref renderData, prevFrame);

            var paramIdOcean = new Vector4[SLICE_COUNT];
            for(int lodIdx = 0; lodIdx < OceanRenderer.Instance.CurrentLodCount; lodIdx++)
            {
                var lt = OceanRenderer.Instance._lods[lodIdx];

                // need to blend out shape if this is the largest lod, and the ocean might get scaled down later (so the largest lod will disappear)
                bool needToBlendOutShape = lodIdx == OceanRenderer.Instance.CurrentLodCount - 1 && OceanRenderer.Instance.ScaleCouldDecrease && blendOut;
                float shapeWeight = needToBlendOutShape ? OceanRenderer.Instance.ViewerAltitudeLevelAlpha : 1f;
                paramIdOcean[lodIdx] = new Vector4(
                    lt._renderData._texelWidth,
                    lt._renderData._textureRes, shapeWeight,
                    1f / lt._renderData._textureRes);
            }
            properties.SetVectorArray(LodTransform.ParamIdOcean(prevFrame), paramIdOcean);
        }

        /// <summary>
        /// Returns index of lod that completely covers the sample area, and contains wavelengths that repeat no more than twice across the smaller
        /// spatial length. If no such lod available, returns -1. This means high frequency wavelengths are filtered out, and the lod index can
        /// be used for each sample in the sample area.
        /// </summary>
        public static int SuggestDataLOD(Rect sampleAreaXZ)
        {
            return SuggestDataLOD(sampleAreaXZ, Mathf.Min(sampleAreaXZ.width, sampleAreaXZ.height));
        }
        public static int SuggestDataLOD(Rect sampleAreaXZ, float minSpatialLength)
        {
            var lodCount = OceanRenderer.Instance.CurrentLodCount;
            for (int lod = 0; lod < lodCount; lod++)
            {
                var lt = OceanRenderer.Instance._lods[lod];

                // Shape texture needs to completely contain sample area
                var lodRect = lt._renderData.RectXZ;
                // Shrink rect by 1 texel border - this is to make finite differences fit as well
                lodRect.x += lt._renderData._texelWidth; lodRect.y += lt._renderData._texelWidth;
                lodRect.width -= 2f * lt._renderData._texelWidth; lodRect.height -= 2f * lt._renderData._texelWidth;
                if (!lodRect.Contains(sampleAreaXZ.min) || !lodRect.Contains(sampleAreaXZ.max))
                    continue;

                // The smallest wavelengths should repeat no more than twice across the smaller spatial length. Unless we're
                // in the last LOD - then this is the best we can do.
                var minWL = OceanRenderer.Instance._lods[lod].MaxWavelength() / 2f;
                if (minWL < minSpatialLength / 2f && lod < lodCount - 1)
                    continue;

                return lod;
            }

            return -1;
        }

        public void AddGerstnerComponent(ShapeGerstnerBatched gerstner)
        {
            if (OceanRenderer.Instance == null)
            {
                // Ocean has unloaded, clear out
                _gerstnerComponents.Clear();
                return;
            }

            _gerstnerComponents.Add(gerstner);
        }

        public void RemoveGerstnerComponent(ShapeGerstnerBatched gerstner)
        {
            if (OceanRenderer.Instance == null)
            {
                // Ocean has unloaded, clear out
                _gerstnerComponents.Clear();
                return;
            }

            _gerstnerComponents.Remove(gerstner);
        }

        // TODO(MRT): Factor these out to be shared with other classes who have to do similar things
        public static string TextureArrayName = "_LD_TexArray_AnimatedWaves_";
        public static int ParamIDTextureArray_ThisFrame = Shader.PropertyToID(TextureArrayName + "ThisFrame");
        public static int ParamIDTextureArray_PrevFrame = Shader.PropertyToID(TextureArrayName + "PrevFrame");
        public static int ParamIdSampler(bool prevFrame = false)
        {
            if(prevFrame)
            {
                return ParamIDTextureArray_PrevFrame;
            }
            else
            {
                return ParamIDTextureArray_ThisFrame;
            }
        }
        protected override int GetParamIdSampler(bool prevFrame = false)
        {
            return ParamIdSampler(prevFrame);
        }
        public static void BindNull(IPropertyWrapper properties, bool prevFrame = false)
        {
            properties.SetTexture(ParamIdSampler(prevFrame), Texture2D.blackTexture);
        }

        public void SetOrigin(Vector3 newOrigin)
        {
            foreach (var gerstner in _gerstnerComponents)
            {
                gerstner.SetOrigin(newOrigin);
            }
        }
    }
}
