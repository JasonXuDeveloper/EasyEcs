/**
 * The WebGL 2 renderer of the live demo. Each frame uploads the frame array
 * (x, y, heat, light per spark, written by demo/EasyEcs.Demo/Frame.cs) in one
 * bufferSubData straight from the WebAssembly memory and draws every spark as
 * one soft point, added into a light buffer that fades instead of being
 * cleared: that fade draws the trails. A last pass tone-maps the light over the
 * theme's background: glowing on dark, ink on light. Nothing is allocated per
 * frame.
 */

export interface Palette {
  /** The card behind the canvas, linear 0..1 */
  background: [number, number, number];
  /** Slow (cold), medium and fast (hot) sparks */
  cold: [number, number, number];
  warm: [number, number, number];
  hot: [number, number, number];
  dark: boolean;
}

export interface Renderer {
  resize(width: number, height: number, scale: number): void;
  /** Fades the trails by `dt` seconds and adds `count` sparks of `heap` from `start` */
  draw(heap: Float32Array, start: number, count: number, dt: number): void;
  /** Shows the light buffer over the background (after draw, or alone to redraw) */
  present(): void;
  setPalette(palette: Palette): void;
  dispose(): void;
}

/** Seconds a trail takes to fade to a third */
const trail = 0.11;
/** Floats per spark in the frame array (Frame.Stride) */
const stride = 4;

const sparkVertex = `#version 300 es
layout(location = 0) in vec4 spark;
uniform vec2 toClip;
uniform float size;
uniform float gain;
uniform vec3 cold, warm, hot;
out vec3 color;
out float light;
void main() {
  gl_Position = vec4(spark.xy * toClip + vec2(-1.0, 1.0), 0.0, 1.0);
  float heat = spark.z;
  color = heat < 0.5 ? mix(cold, warm, heat * 2.0) : mix(warm, hot, heat * 2.0 - 1.0);
  // Slow sparks dim: the moving rings and their trails carry the picture
  light = spark.w * gain * (0.1 + 0.9 * heat * heat);
  // A flash (light over 1) is drawn larger as well as brighter
  gl_PointSize = size * (0.75 + 0.45 * spark.w);
}`;

const sparkFragment = `#version 300 es
precision mediump float;
in vec3 color;
in float light;
out vec4 result;
void main() {
  vec2 d = gl_PointCoord * 2.0 - 1.0;
  float r2 = dot(d, d);
  if (r2 > 1.0) discard;
  // A bright core in a soft halo
  float glow = (exp(-r2 * 9.0) * 0.8 + exp(-r2 * 2.5) * 0.2) * light;
  result = vec4(color * glow, glow);
}`;

const quadVertex = `#version 300 es
out vec2 uv;
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  uv = p;
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

const fadeFragment = `#version 300 es
precision mediump float;
out vec4 result;
void main() { result = vec4(0.0); }`;

const presentFragment = `#version 300 es
precision highp float;
uniform sampler2D lightBuffer;
uniform vec3 background;
uniform bool dark;
in vec2 uv;
out vec4 result;
vec3 toSrgb(vec3 c) {
  return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}
void main() {
  vec4 l = texture(lightBuffer, uv);
  vec3 c;
  if (dark) {
    // Light adds up and saturates softly towards white
    c = background + (1.0 - exp(-l.rgb * 1.6));
  } else {
    // Ink: the sparks' own color covers the paper as they pile up
    float cover = 1.0 - exp(-l.a * 2.5);
    c = mix(background, l.rgb / max(l.a, 1e-4), cover);
  }
  result = vec4(toSrgb(clamp(c, 0.0, 1.0)), 1.0);
}`;

function compile(
  gl: WebGL2RenderingContext,
  vertex: string,
  fragment: string,
): WebGLProgram {
  const program = gl.createProgram();
  for (const [type, source] of [
    [gl.VERTEX_SHADER, vertex],
    [gl.FRAGMENT_SHADER, fragment],
  ] as const) {
    const shader = gl.createShader(type);
    if (!shader) throw new Error('WebGL: no shader');
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS))
      throw new Error(`WebGL: ${gl.getShaderInfoLog(shader)}`);
    gl.attachShader(program, shader);
    gl.deleteShader(shader);
  }
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS))
    throw new Error(`WebGL: ${gl.getProgramInfoLog(program)}`);
  return program;
}

/** A renderer on `canvas`, or undefined when the browser has no WebGL 2 */
export function createRenderer(
  canvas: HTMLCanvasElement,
  palette: Palette,
): Renderer | undefined {
  const gl = canvas.getContext('webgl2', {
    alpha: false,
    antialias: false,
    depth: false,
    stencil: false,
    premultipliedAlpha: false,
    powerPreference: 'high-performance',
  });
  if (!gl) return undefined;
  // Half floats keep faint trails from banding; 8 bits per channel otherwise
  const float = gl.getExtension('EXT_color_buffer_float') !== null;

  const sparks = compile(gl, sparkVertex, sparkFragment);
  const fade = compile(gl, quadVertex, fadeFragment);
  const present = compile(gl, quadVertex, presentFragment);
  const at = (program: WebGLProgram, name: string) =>
    gl.getUniformLocation(program, name);
  const u = {
    toClip: at(sparks, 'toClip'),
    size: at(sparks, 'size'),
    gain: at(sparks, 'gain'),
    cold: at(sparks, 'cold'),
    warm: at(sparks, 'warm'),
    hot: at(sparks, 'hot'),
    lightBuffer: at(present, 'lightBuffer'),
    background: at(present, 'background'),
    dark: at(present, 'dark'),
  };

  const bind = (program: WebGLProgram) =>
    // biome-ignore lint/correctness/useHookAtTopLevel: WebGL's useProgram, not a React hook
    gl.useProgram(program);

  const vao = gl.createVertexArray();
  const buffer = gl.createBuffer();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 4, gl.FLOAT, false, stride * 4, 0);
  gl.bindVertexArray(null);
  let capacity = 0;

  const texture = gl.createTexture();
  const framebuffer = gl.createFramebuffer();
  let width = 1;
  let height = 1;
  let cssWidth = 1;
  let cssHeight = 1;
  let pixelScale = 1;
  let colors = palette;

  return {
    resize(w, h, scale) {
      cssWidth = w;
      cssHeight = h;
      pixelScale = scale;
      width = Math.max(1, Math.round(w * scale));
      height = Math.max(1, Math.round(h * scale));
      canvas.width = width;
      canvas.height = height;
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(
        gl.TEXTURE_2D,
        0,
        float ? gl.RGBA16F : gl.RGBA8,
        width,
        height,
        0,
        gl.RGBA,
        float ? gl.HALF_FLOAT : gl.UNSIGNED_BYTE,
        null,
      );
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
      gl.framebufferTexture2D(
        gl.FRAMEBUFFER,
        gl.COLOR_ATTACHMENT0,
        gl.TEXTURE_2D,
        texture,
        0,
      );
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
    },

    draw(heap, start, count, dt) {
      gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
      gl.viewport(0, 0, width, height);
      gl.enable(gl.BLEND);

      // The trails: what is in the buffer keeps exp(-dt / trail) of its light
      bind(fade);
      gl.blendFunc(gl.ZERO, gl.CONSTANT_ALPHA);
      gl.blendColor(0, 0, 0, Math.exp(-dt / trail));
      gl.drawArrays(gl.TRIANGLES, 0, 3);

      if (count > 0) {
        const floats = count * stride;
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        if (floats > capacity) {
          capacity = floats;
          gl.bufferData(gl.ARRAY_BUFFER, capacity * 4, gl.DYNAMIC_DRAW);
        }
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, heap, start, floats);

        bind(sparks);
        gl.uniform2f(u.toClip, 2 / cssWidth, -2 / cssHeight);
        // Fewer, larger sparks on a small canvas and dimmer ones in a crowd: the same picture at
        // any size and count
        const area = (cssWidth * cssHeight) / (720 * 440);
        const crowd = Math.max(1, count / 10_000) ** 0.7;
        gl.uniform1f(u.size, 7 * pixelScale * Math.min(1, Math.sqrt(area)));
        gl.uniform1f(
          u.gain,
          ((colors.dark ? 0.2 : 0.45) *
            Math.min(1.5, Math.max(0.7, Math.sqrt(area)))) /
            crowd,
        );
        gl.uniform3fv(u.cold, colors.cold);
        gl.uniform3fv(u.warm, colors.warm);
        gl.uniform3fv(u.hot, colors.hot);
        gl.blendFunc(gl.ONE, gl.ONE);
        gl.bindVertexArray(vao);
        gl.drawArrays(gl.POINTS, 0, count);
        gl.bindVertexArray(null);
      }
      gl.disable(gl.BLEND);
    },

    present() {
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, width, height);
      bind(present);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.uniform1i(u.lightBuffer, 0);
      gl.uniform3fv(u.background, colors.background);
      gl.uniform1i(u.dark, colors.dark ? 1 : 0);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    },

    setPalette(next) {
      colors = next;
    },

    dispose() {
      gl.deleteBuffer(buffer);
      gl.deleteVertexArray(vao);
      gl.deleteTexture(texture);
      gl.deleteFramebuffer(framebuffer);
      gl.deleteProgram(sparks);
      gl.deleteProgram(fade);
      gl.deleteProgram(present);
    },
  };
}
