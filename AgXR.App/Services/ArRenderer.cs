using System;
using Android.Opengl;
using Java.Nio;
using Google.AR.Core;

namespace AgXR.App.Services
{
    public class ArRenderer
    {
        private int _textureId = -1;
        private int _programId = -1;
        private int _positionAttrib = -1;
        private int _texCoordAttrib = -1;
        private int _textureUniform = -1;

        // Quad vertices for full screen
        private readonly float[] _quadVertices = {
            -1.0f, -1.0f, 0.0f,
            -1.0f, +1.0f, 0.0f,
            +1.0f, -1.0f, 0.0f,
            +1.0f, +1.0f, 0.0f,
        };

        private readonly float[] _quadTexCoords = {
            0.0f, 1.0f,
            0.0f, 0.0f,
            1.0f, 1.0f,
            1.0f, 0.0f,
        };

        private FloatBuffer? _vertexBuffer;
        private FloatBuffer? _texCoordBuffer;

        private const string VertexShaderCode = @"
            attribute vec4 a_Position;
            attribute vec2 a_TexCoord;
            varying vec2 v_TexCoord;
            void main() {
                gl_Position = a_Position;
                v_TexCoord = a_TexCoord;
            }";

        private const string FragmentShaderCode = @"
            #extension GL_OES_EGL_image_external : require
            precision mediump float;
            varying vec2 v_TexCoord;
            uniform samplerExternalOES u_Texture;
            void main() {
                gl_FragColor = texture2D(u_Texture, v_TexCoord);
            }";

        public int TextureId => _textureId;
        public int ProgramId => _programId;
        public int PositionAttrib => _positionAttrib;
        public int TexCoordAttrib => _texCoordAttrib;
        public int TextureUniform => _textureUniform;
        public FloatBuffer? VertexBuffer => _vertexBuffer;
        public FloatBuffer? TexCoordBuffer => _texCoordBuffer;

        public void CreateOnGlThread()
        {
            // Generate external texture for camera feed
            var textures = new int[1];
            GLES20.GlGenTextures(1, textures, 0);
            _textureId = textures[0];

            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _textureId);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlNearest);

            // Compile shaders
            int vertexShader = LoadShader(GLES20.GlVertexShader, VertexShaderCode);
            int fragmentShader = LoadShader(GLES20.GlFragmentShader, FragmentShaderCode);

            _programId = GLES20.GlCreateProgram();
            GLES20.GlAttachShader(_programId, vertexShader);
            GLES20.GlAttachShader(_programId, fragmentShader);
            GLES20.GlLinkProgram(_programId);

            _positionAttrib = GLES20.GlGetAttribLocation(_programId, "a_Position");
            _texCoordAttrib = GLES20.GlGetAttribLocation(_programId, "a_TexCoord");
            _textureUniform = GLES20.GlGetUniformLocation(_programId, "u_Texture");

            // Setup buffers
            _vertexBuffer = ByteBuffer.AllocateDirect(_quadVertices.Length * 4)
                .Order(ByteOrder.NativeOrder()).AsFloatBuffer();
            _vertexBuffer.Put(_quadVertices);
            _vertexBuffer.Position(0);

            _texCoordBuffer = ByteBuffer.AllocateDirect(_quadTexCoords.Length * 4)
                .Order(ByteOrder.NativeOrder()).AsFloatBuffer();
            _texCoordBuffer.Put(_quadTexCoords);
            _texCoordBuffer.Position(0);
        }

        public void Draw(Frame frame)
        {
            if (frame == null) return;

            // Adjust texture coordinates to fit screen aspect ratio
            if (frame.HasDisplayGeometryChanged && _texCoordBuffer != null)
            {
                // Reset to identity UVs first — TransformDisplayUvCoords is not idempotent,
                // so reusing the buffer as both input and output compounds rotations across resumes.
                _texCoordBuffer.Position(0);
                _texCoordBuffer.Put(_quadTexCoords);
                _texCoordBuffer.Position(0);
                frame.TransformDisplayUvCoords(_texCoordBuffer, _texCoordBuffer);
            }

            GLES20.GlUseProgram(_programId);

            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _textureId);
            GLES20.GlUniform1i(_textureUniform, 0);

            GLES20.GlEnableVertexAttribArray(_positionAttrib);
            GLES20.GlVertexAttribPointer(_positionAttrib, 3, GLES20.GlFloat, false, 0, _vertexBuffer);

            GLES20.GlEnableVertexAttribArray(_texCoordAttrib);
            GLES20.GlVertexAttribPointer(_texCoordAttrib, 2, GLES20.GlFloat, false, 0, _texCoordBuffer);

            GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);

            GLES20.GlDisableVertexAttribArray(_positionAttrib);
            GLES20.GlDisableVertexAttribArray(_texCoordAttrib);
        }

        private int LoadShader(int type, string shaderCode)
        {
            int shader = GLES20.GlCreateShader(type);
            GLES20.GlShaderSource(shader, shaderCode);
            GLES20.GlCompileShader(shader);
            return shader;
        }
    }
}
