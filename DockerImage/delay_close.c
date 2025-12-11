/*
 * delay_close.c - LD_PRELOAD library to force 4-way TCP handshake
 * 
 * This library intercepts BOTH read() and close() syscalls to force
 * Linux to send ACK separately from FIN during TCP connection close.
 * 
 * When read() returns 0 (EOF/FIN received), we add a 50ms delay to
 * allow the kernel's delayed ACK timer to expire. This ensures the ACK
 * is sent BEFORE the application calls close() which would send FIN.
 * 
 * This produces the standard 4-way handshake sequence:
 *   FIN-ACK -> ACK -> FIN-ACK -> ACK (4 packets)
 * 
 * Instead of the Linux optimized 3-way close:
 *   FIN-ACK -> FIN-ACK -> ACK (3 packets, ACK piggybacked on FIN)
 * 
 * Usage:
 *   LD_PRELOAD=/usr/lib/libdelay_close.so ./your_application
 * 
 * Build:
 *   gcc -fPIC -shared -o libdelay_close.so delay_close.c -ldl
 */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <errno.h>

/* Pointers to the real functions */
static ssize_t (*real_read)(int, void*, size_t) = NULL;
static ssize_t (*real_recv)(int, void*, size_t, int) = NULL;

/*
 * Initialize the real function pointers on first use.
 */
static void init_real_funcs(void) {
    if (!real_read) {
        real_read = dlsym(RTLD_NEXT, "read");
    }
    if (!real_recv) {
        real_recv = dlsym(RTLD_NEXT, "recv");
    }
}

/*
 * Check if a file descriptor is a socket
 */
static int is_socket(int fd) {
    struct stat st;
    if (fstat(fd, &st) == -1) {
        return 0;
    }
    return S_ISSOCK(st.st_mode);
}

/*
 * Intercepted read() function.
 * 
 * When read() returns 0 (EOF) on a socket, we add a 50ms delay.
 * This allows the kernel's delayed ACK timer to expire, forcing
 * the ACK to be sent as a separate packet before the app closes.
 */
ssize_t read(int fd, void *buf, size_t count) {
    init_real_funcs();
    
    ssize_t result = real_read(fd, buf, count);
    
    /* If read returned 0 on a socket, delay to force ACK to be sent */
    if (result == 0 && is_socket(fd)) {
        usleep(50000);  /* 50ms delay */
    }
    
    return result;
}

/*
 * Intercepted recv() function.
 * 
 * Same logic as read() - when recv() returns 0 (EOF) on a socket,
 * we add a 50ms delay to force the ACK to be sent separately.
 */
ssize_t recv(int fd, void *buf, size_t len, int flags) {
    init_real_funcs();
    
    ssize_t result = real_recv(fd, buf, len, flags);
    
    /* If recv returned 0 on a socket, delay to force ACK to be sent */
    if (result == 0 && is_socket(fd)) {
        usleep(50000);  /* 50ms delay */
    }
    
    return result;
}
