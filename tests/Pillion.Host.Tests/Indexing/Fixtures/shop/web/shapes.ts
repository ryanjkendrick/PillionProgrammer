export interface Shape {
  area(): number;
}

export class Circle implements Shape {
  constructor(private readonly radius: number) {}

  area(): number {
    return Math.PI * this.radius * this.radius;
  }
}

export class Rectangle implements Shape {
  constructor(private readonly width: number, private readonly height: number) {}

  area(): number {
    return this.width * this.height;
  }
}
