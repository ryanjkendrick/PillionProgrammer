package server

import (
	"fmt"
	"net/http"
)

// Server serves requests.
type Server struct {
	addr string
}

const defaultAddr = ":8080"

// New creates a server.
func New(addr string) *Server {
	if addr == "" {
		addr = defaultAddr
	}
	return &Server{addr: addr}
}

// Start listens.
func (s *Server) Start() error {
	fmt.Println("listening on", s.addr)
	return http.ListenAndServe(s.addr, nil)
}
