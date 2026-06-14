// The following ifdef block is the standard way of creating macros which make exporting
// from a DLL simpler. All files within this DLL are compiled with the MASTERSPLINTERLOGIC_EXPORTS
// symbol defined on the command line. This symbol should not be defined on any project
// that uses this DLL. This way any other project whose source files include this file see
// MASTERSPLINTERLOGIC_API functions as being imported from a DLL, whereas this DLL sees symbols
// defined with this macro as being exported.
#ifdef MASTERSPLINTERLOGIC_EXPORTS
#define MASTERSPLINTERLOGIC_API __declspec(dllexport)
#else
#define MASTERSPLINTERLOGIC_API __declspec(dllimport)
#endif

// This class is exported from the dll
class MASTERSPLINTERLOGIC_API CMasterSplinterLogic {
public:
	CMasterSplinterLogic(void);
	// TODO: add your methods here.
};

extern MASTERSPLINTERLOGIC_API int nMasterSplinterLogic;

MASTERSPLINTERLOGIC_API int fnMasterSplinterLogic(void);
