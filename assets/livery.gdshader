shader_type canvas_item;

// Painted bodywork of a vehicle sprite, recoloured (issue #10).
//
// TEXTURE is the photo crop of the vehicle (assets/car_<vehicle>.png, from a
// green-screen top-down photo); mask_tex marks which of its pixels are painted
// metal (assets/car_<vehicle>_mask.png, authored in tools/make_car_sprites.py).
// Only those pixels take the livery, so glass stays dark, tyres stay black,
// chrome and the number plate stay themselves — the requirement this whole
// feature exists to satisfy ("I don't want windows to become pink just because
// I change the car colour").
//
// The livery is not painted flat on top of the photo: the pixel's own luminance
// is kept as shading, so a lit roof stays lighter than a shaded door and the
// panel still reads as a photographed car body rather than a sticker.

uniform sampler2D mask_tex : filter_linear_mipmap;
uniform vec4 livery : source_color = vec4(1.0);
uniform float strength : hint_range(0.0, 1.0) = 1.0;

// Mid grey as the photo's own reference: a pixel at this luminance gets the
// livery unmodified, brighter ones get it brighter, darker ones darker.
const float PHOTO_MID = 0.45;
const float SHADE_MIN = 0.35;
const float SHADE_MAX = 1.35;   // specular hits must not blow out to white

void fragment() {
    vec4 photo = texture(TEXTURE, UV);
    float m = texture(mask_tex, UV).r * photo.a * strength;
    float lum = dot(photo.rgb, vec3(0.299, 0.587, 0.114));
    float shade = clamp(lum / PHOTO_MID, SHADE_MIN, SHADE_MAX);
    vec3 tinted = clamp(livery.rgb * shade, vec3(0.0), vec3(1.0));
    COLOR = vec4(mix(photo.rgb, tinted, m), photo.a);
}
