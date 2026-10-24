"use strict";
const express = require("express");

// Builds the router.
function buildRouter(app) {
  app.get("/", (req, res) => res.send("ok"));
  return app;
}

class Store {
  items = [];

  add(item) {
    this.items.push(item);
  }

  get size() {
    return this.items.length;
  }
}

export const handler = async (event) => {
  return { statusCode: 200, body: JSON.stringify(event) };
};

module.exports = { buildRouter, Store };
