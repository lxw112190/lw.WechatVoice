/* Export shim: the SILK SDK sources do not export symbols themselves.
   Only the decoder entry points the engine needs are exposed. */
#include <stdint.h>
#include "SKP_Silk_SDK_API.h"

__declspec(dllexport) int silk_sdk_get_decoder_size(int32_t *sizeBytes) {
    return SKP_Silk_SDK_Get_Decoder_Size(sizeBytes);
}
__declspec(dllexport) int silk_sdk_init_decoder(void *decState) {
    return SKP_Silk_SDK_InitDecoder(decState);
}
__declspec(dllexport) int silk_sdk_decode(void *decState, SKP_SILK_SDK_DecControlStruct *decControl,
    int lostFlag, const uint8_t *inData, int32_t nBytesIn, int16_t *samplesOut, int16_t *nSamplesOut) {
    return SKP_Silk_SDK_Decode(decState, decControl, lostFlag, inData, nBytesIn, samplesOut, nSamplesOut);
}
