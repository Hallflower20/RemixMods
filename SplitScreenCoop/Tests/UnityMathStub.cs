using System;

// Only the managed Vector2/Mathf operations used by the solver. This makes the
// geometry tests runnable without starting Unity or loading Rain World DLLs.
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public static Vector2 right => new Vector2(1f, 0f);
        public static Vector2 up => new Vector2(0f, 1f);
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector2 normalized => magnitude > 1e-6f ? this / magnitude : zero;
        public void Normalize() { this = normalized; }
        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * Mathf.Clamp01(t);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float n) => new Vector2(a.x * n, a.y * n);
        public static Vector2 operator *(float n, Vector2 a) => a * n;
        public static Vector2 operator /(Vector2 a, float n) => new Vector2(a.x / n, a.y / n);
        public override string ToString() => "(" + x + ", " + y + ")";
    }

    public static class Mathf
    {
        public const float Infinity = float.PositiveInfinity;
        public static float Abs(float v) => Math.Abs(v);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        public static float Clamp(float a, float lo, float hi) => Math.Max(lo, Math.Min(a, hi));
        public static float Clamp01(float a) => Clamp(a, 0f, 1f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float SmoothDamp(float current, float target, ref float velocity,
            float smoothTime, float maxSpeed, float deltaTime)
        {
            smoothTime = Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * deltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float originalTarget = target;
            float maxChange = maxSpeed * smoothTime;
            change = Clamp(change, -maxChange, maxChange);
            target = current - change;
            float temp = (velocity + omega * change) * deltaTime;
            velocity = (velocity - omega * temp) * exp;
            float result = target + (change + temp) * exp;
            if ((originalTarget - current > 0f) == (result > originalTarget))
            {
                result = originalTarget;
                velocity = (result - originalTarget) / deltaTime;
            }
            return result;
        }
    }
}
