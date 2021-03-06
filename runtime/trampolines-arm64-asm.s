#
# store all parameters in a consistent way, and send it off to managed code.
# we need to store:
#   x0-x9, x16
#   q0-q7
#   x29, x30 (for the stack frame)
#   an unknown amount of stack space, but we can pass a pointer to the start of this area.
# in total we need 11*64bits registers + 8*128bits registers + 2*64bits register = 232 bytes.
# the 128 bit registers need to be 16-byte aligned, so there's a register-sized padding before the qX registers, thus total 240 bytes.
# in addition we store a copy of x0 and x1 as _self and _sel, so that we can get those values even if
# x0 and x1 change (which they might very well do after processing the return value, so 240 + 2*8 = 256 bytes.
#
# upon return we may need to write to:
#   x0, x1
#   q0, q1, q2, q3
#

#if __arm64__

.subsections_via_symbols
.text
.align 2
_xamarin_arm64_common_trampoline:
	mov x9, sp ;#Save sp to a temporary register
	sub sp, sp, #256 ;# allocate 224 bytes from the stack (stack must always be 16-byte aligned) + 16 bytes for the stack frame + 8*2 bytes for _self and _sel

	# Create stack frame
	stp x29, x30, [sp, #0x00]
	mov x29, sp

	stp x16, x9, [sp, #0x10]
	stp  x0, x1, [sp, #0x20]
	stp  x2, x3, [sp, #0x30]
	stp  x4, x5, [sp, #0x40]
	stp  x6, x7, [sp, #0x50]
	str  x8,     [sp, #0x60]

	stp  q0, q1, [sp, #0x70]
	stp  q2, q3, [sp, #0x90]
	stp  q4, q5, [sp, #0xb0]
	stp  q6, q7, [sp, #0xd0]

	add x0, sp, #0x10 ;# the first two pointers are the stack frame (x29, x30), the rest is a pointer to a XamarinCallState struct.
	bl	_xamarin_arch_trampoline

	# get return value(s)

	ldp x16, x9, [sp, #0x10]
	ldp  x0, x1, [sp, #0x20]
	ldp  x2, x3, [sp, #0x30]
	ldp  x4, x5, [sp, #0x40]
	ldp  x6, x7, [sp, #0x50]
	ldr  x8,     [sp, #0x60]

	ldp  q0, q1, [sp, #0x70]
	ldp  q2, q3, [sp, #0x90]
	ldp  q4, q5, [sp, #0xb0]
	ldp  q6, q7, [sp, #0xd0]

	ldp	x29, x30, [sp, #0x00]
	add sp, sp, #256 ;# deallocate 224 bytes from the stack + 16 bytes for stack frame + 8*2 bytes for _self and _sel

	ret

#
# trampolines
#

.globl _xamarin_trampoline
_xamarin_trampoline:
	mov	x16, #0x0
	b	_xamarin_arm64_common_trampoline

.globl _xamarin_static_trampoline
_xamarin_static_trampoline:
	mov	x16, #0x1
	b	_xamarin_arm64_common_trampoline

.globl _xamarin_ctor_trampoline
_xamarin_ctor_trampoline:
	mov	x16, #0x2
	b _xamarin_arm64_common_trampoline

.globl _xamarin_fpret_single_trampoline
_xamarin_fpret_single_trampoline:
	mov	x16, #0x4
	b _xamarin_arm64_common_trampoline

.globl _xamarin_static_fpret_single_trampoline
_xamarin_static_fpret_single_trampoline:
	mov	x16, #0x5
	b _xamarin_arm64_common_trampoline

.globl _xamarin_fpret_double_trampoline
_xamarin_fpret_double_trampoline:
	mov	x16, #0x8
	b _xamarin_arm64_common_trampoline

.globl _xamarin_static_fpret_double_trampoline
_xamarin_static_fpret_double_trampoline:
	mov	x16, #0x9
	b _xamarin_arm64_common_trampoline

.globl _xamarin_stret_trampoline
_xamarin_stret_trampoline:
	mov	x16, #0x10
	b _xamarin_arm64_common_trampoline

.globl _xamarin_static_stret_trampoline
_xamarin_static_stret_trampoline:
	mov	x16, #0x11
	b _xamarin_arm64_common_trampoline

.globl _xamarin_longret_trampoline
_xamarin_longret_trampoline:
	mov	x16, #0x20
	b _xamarin_arm64_common_trampoline

.globl _xamarin_static_longret_trampoline
_xamarin_static_longret_trampoline:
	mov	x16, #0x21
	b _xamarin_arm64_common_trampoline

# etc...

#endif
